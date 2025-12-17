using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Trove.SpatialQueries
{
    public struct BVHNode : IComparable<BVHNode>
    {
        public AABB AABB;
        public int DataIndex; // For leaf nodes this is index of their data, but for hierarchy nodes this is index of their children
        public uint MortonCode; 

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsValid()
        {
            return DataIndex >= 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(BVHNode other)
        {
            return MortonCode.CompareTo(other.MortonCode) ;
        }
    }

    public struct StartIndexAndCount
    {
        public int StartIndex;
        public int Count;
    }

    internal struct SortingNode
    {
        public int Next;
        public int NodeIndex;
    }

    public struct BVH<TNodeData> where TNodeData : unmanaged
    {
        internal NativeList<BVHNode> UnsortedNodes;
        internal NativeList<BVHNode> SortedNodes;
        internal NativeList<TNodeData> LeafNodeDatas;
        internal NativeList<StartIndexAndCount> NodeLevelStartIndexesAndCounts;
        internal NativeReference<AABB> SceneAABB;
        internal NativeList<UnsafeList<int>> SortingNodeBuckets;
        internal NativeList<UnsafeList<int>> SortingNodeIndexes;

        public static BVH<TNodeData> Create(Allocator allocator, int initialElementsCapacity)
        {
            BVH<TNodeData> bvh = new BVH<TNodeData>();
            bvh.UnsortedNodes = new NativeList<BVHNode>(
                BVHUtils.ComputeTotalNodesCountForEntries(initialElementsCapacity), 
                allocator);
            bvh.SortedNodes = new NativeList<BVHNode>(bvh.UnsortedNodes.Capacity, allocator);
            bvh.LeafNodeDatas = new NativeList<TNodeData>(initialElementsCapacity, allocator);
            bvh.NodeLevelStartIndexesAndCounts = new NativeList<StartIndexAndCount>(32, allocator);
            bvh.SceneAABB = new NativeReference<AABB>(allocator);
            bvh.SortingNodeBuckets = new NativeList<UnsafeList<int>>(BVHUtils.NbValuesPerMortonCodeDigit, allocator);
            bvh.SortingNodeBuckets.Resize(BVHUtils.NbValuesPerMortonCodeDigit, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < BVHUtils.NbValuesPerMortonCodeDigit; i++)
            {
                bvh.SortingNodeBuckets[i] = new UnsafeList<int>(256, allocator);
            }
            bvh.SortingNodeIndexes = new NativeList<UnsafeList<int>>(2, allocator);
            bvh.SortingNodeIndexes.Resize(2, NativeArrayOptions.ClearMemory);
            bvh.SortingNodeIndexes[0] = new UnsafeList<int>(bvh.UnsortedNodes.Capacity, allocator);
            bvh.SortingNodeIndexes[1] = new UnsafeList<int>(bvh.UnsortedNodes.Capacity, allocator);
            return bvh;
        }

        public void Dispose(JobHandle jobHandle)
        {
            if (UnsortedNodes.IsCreated)
            {
                UnsortedNodes.Dispose(jobHandle);
            }
            
            if (SortedNodes.IsCreated)
            {
                SortedNodes.Dispose(jobHandle);
            }

            if (LeafNodeDatas.IsCreated)
            {
                LeafNodeDatas.Dispose(jobHandle);
            }

            if (NodeLevelStartIndexesAndCounts.IsCreated)
            {
                NodeLevelStartIndexesAndCounts.Dispose(jobHandle);
            }

            if (SceneAABB.IsCreated)
            {
                SceneAABB.Dispose(jobHandle);
            }

            if (SortingNodeBuckets.IsCreated)
            {
                for (int i = 0; i < SortingNodeBuckets.Length; i++)
                {
                    if (SortingNodeBuckets[i].IsCreated)
                    {
                        SortingNodeBuckets[i].Dispose(jobHandle);
                    }
                }
                
                SortingNodeBuckets.Dispose(jobHandle);
            }

            if (SortingNodeIndexes.IsCreated)
            {
                for (int i = 0; i < SortingNodeIndexes.Length; i++)
                {
                    if (SortingNodeIndexes[i].IsCreated)
                    {
                        SortingNodeIndexes[i].Dispose(jobHandle);
                    }
                }
                
                SortingNodeIndexes.Dispose(jobHandle);
            }
        }
        
        public void Clear()
        {
            UnsortedNodes.Clear();
            SortedNodes.Clear();
            LeafNodeDatas.Clear();
            NodeLevelStartIndexesAndCounts.Clear();
            SceneAABB.Value = AABB.GetEmpty();
            for (int i = 0; i < SortingNodeBuckets.Length; i++)
            {
                UnsafeList<int> bucketList = SortingNodeBuckets[i];
                bucketList.Clear();
                SortingNodeBuckets[i] = bucketList;
            }
            for (int i = 0; i < SortingNodeIndexes.Length; i++)
            {
                UnsafeList<int> indexList = SortingNodeIndexes[i];
                indexList.Clear();
                SortingNodeIndexes[i] = indexList;
            }
        }

        public void Add(in TNodeData nodeData, in AABB aabb)
        {
            AABB sceneAABB = SceneAABB.Value;
            sceneAABB.Include(aabb);
            SceneAABB.Value = sceneAABB; 
            
            UnsortedNodes.Add(new BVHNode
            {
                AABB = aabb,
            });
            LeafNodeDatas.Add(nodeData);
        }

        public JobHandle ScheduleClearJob(JobHandle dep)
        {
            dep = new BVHClearJob
            {
                BVH = this,
            }.Schedule(dep);
            
            return dep;
        }

        public JobHandle ScheduleBuildJobs(bool useParallelSort, JobHandle dep)
        {
            int workerCount = JobsUtility.JobWorkerCount;

            dep = new BVHComputeMortonCodesAndDataIndexesJob
            {
                WorkerCount = workerCount,
                SceneAABB = SceneAABB,
                UnsortedNodes = UnsortedNodes,
            }.ScheduleParallel(workerCount, 1, dep);
            
            // Parallel radix sort nodes by morton code
            if(useParallelSort)
            {
                dep = new BVHSortNodesInitialJob
                {
                    WorkerCount = workerCount,
                    UnsortedNodes = UnsortedNodes,
                    SortingNodeBuckets = SortingNodeBuckets,
                    SortingNodeIndexes = SortingNodeIndexes,
                }.Schedule(dep);

                // For each digit index...
                for (int digitIndex = 0; digitIndex < BVHUtils.NbDigitsMortonCode; digitIndex++)
                {
                    // Launch one job per digit value...
                    dep = new BVHSortNodesParallelJob
                    {
                        WorkerCount = workerCount,
                        DigitIndex = digitIndex,
                        UnsortedNodes = UnsortedNodes,
                        SortingNodeBuckets = SortingNodeBuckets,
                        SortingNodeIndexes = SortingNodeIndexes,
                    }.ScheduleParallel(workerCount, 1, dep);

                    // Then merge
                    dep = new BVHSortNodesMergeJob()
                    {
                        WorkerCount = workerCount,
                        DigitIndex = digitIndex,
                        SortingNodeBuckets = SortingNodeBuckets,
                        SortingNodeIndexes = SortingNodeIndexes,
                    }.Schedule(dep);
                }

                dep = new BVHSortNodesFinalJob()
                {
                    UnsortedNodes = UnsortedNodes,
                    SortedNodes = SortedNodes,
                    SortingNodeIndexes = SortingNodeIndexes,
                }.Schedule(dep);
            }
            else
            {
                dep = new BVHSingleSortJob
                {
                    UnsortedNodes = UnsortedNodes,
                    SortedNodes = SortedNodes,
                }.Schedule(dep);
            }

            dep = new BVHBuildHierarchyJob
            {
                SortedNodes = SortedNodes,
                NodeLevelStartIndexesAndCounts = NodeLevelStartIndexesAndCounts,
            }.Schedule(dep);

            return dep;
        }

        public unsafe void GetNodes(out UnsafeList<BVHNode> nodes,
            out UnsafeList<StartIndexAndCount> nodeLevelStartIndexesAndCounts)
        {
            nodes = (*SortedNodes.GetUnsafeList());
            nodeLevelStartIndexesAndCounts = (*NodeLevelStartIndexesAndCounts.GetUnsafeList());
        }

        public void QueryAABBRecursive(in AABB aabb, ref UnsafeList<TNodeData> results)
        {
            // start at root node
            QueryAABBRecursiveInternal(SortedNodes.Length - 1, in aabb, ref results);
        }

        internal void QueryAABBRecursiveInternal(int nodeIndex, in AABB aabb, ref UnsafeList<TNodeData> results)
        {
            BVHNode queriedNode = SortedNodes[nodeIndex];
            if (queriedNode.IsValid() && aabb.OverlapAABB(queriedNode.AABB))
            {
                if (nodeIndex < LeafNodeDatas.Length)
                {
                    results.Add(LeafNodeDatas[queriedNode.DataIndex]);
                }
                else
                {
                    // Query both child nodes
                    QueryAABBRecursiveInternal(queriedNode.DataIndex, in aabb, ref results);
                    QueryAABBRecursiveInternal(queriedNode.DataIndex + 1, in aabb, ref results);
                }
            }
        }

        public void QueryAABBStack(in AABB aabb, ref UnsafeList<int> workStack, ref UnsafeList<TNodeData> results)
        {
            // Add root node to stack
            workStack.Clear();
            workStack.Add(SortedNodes.Length - 1);

            for (int i = 0; i < workStack.Length; i++)
            {
                int nodeIndex = workStack[i];
                BVHNode queriedNode = SortedNodes[nodeIndex];
                if (queriedNode.IsValid() && aabb.OverlapAABB(queriedNode.AABB))
                {
                    if (nodeIndex < LeafNodeDatas.Length)
                    {
                        results.Add(LeafNodeDatas[queriedNode.DataIndex]);
                    }
                    else
                    {
                        // Add both child nodes to stack
                        workStack.Add(queriedNode.DataIndex);
                        workStack.Add(queriedNode.DataIndex + 1);
                    }
                }
            }
        }

        public void QueryRay(in AABB aabb, ref UnsafeList<TNodeData> results)
        {
            // TODO
        }
    
        [BurstCompile]
        public struct BVHClearJob : IJob
        {
            public BVH<TNodeData> BVH;
        
            public void Execute()
            {
                BVH.Clear();
            }
        }
    }

    internal static class BVHUtils
    {
        internal const int NbDigitsMortonCode = 10; // The number of digits in the max value of a uint (4294967295)
        internal const int NbValuesPerMortonCodeDigit = 10; // The number of values a digit can have (0 to 9)
        
        internal static int ComputeTotalNodesCountForEntries(int entriesCount)
        {
            // Make entries count even
            if (entriesCount % 2 != 0)
            {
                entriesCount++;
            }
            
            float entriesCountFloat = (float)entriesCount;
            
            while (entriesCountFloat > 1f)
            {
                entriesCountFloat *= 0.5f;
                entriesCount += (int)math.ceil(entriesCountFloat);
            }
            
            return entriesCount;
        }
        
        /// <summary>
        /// https://developer.nvidia.com/blog/thinking-parallel-part-iii-tree-construction-gpu/
        ///
        /// The nPos is a normalized position from 0,0,0 to 1,1,1
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint ComputeMortonCode(float3 nPos)
        {
            // The normalized position coords get turned into a 0f to 1023f range. We want the 1023 range because
            // 1023 as a uint in binary is 1111111111, which is 10 bits. Later this will allow us to store the 3
            // position coords as interleaved shifted bits in a 32 bit uint.
            nPos = math.min(math.max(nPos * 1024.0f, 0.0f), 1023.0f);
            
            // By casting the 0-to-1023 number to uint, we get 10 significant  bits to work with. (1023 in binary
            // is 1111111111). We then "expand" the bits of each value to make space for bit interleaving. 
            uint expandedX = ExpandBits((uint)nPos.x);
            uint expandedY = ExpandBits((uint)nPos.y);
            uint expandedZ = ExpandBits((uint)nPos.z);

            // This is what creates the "interleaving" of the expanded bits. Multiplication by 4 "shifts" the bits
            // by two spaces, and multiplying by 2 shifts by one space.
            return (expandedX * 4) + (expandedY * 2) + expandedZ;
        }
        
        /// <summary>
        /// https://developer.nvidia.com/blog/thinking-parallel-part-iii-tree-construction-gpu/
        ///
        /// This takes a value with 10 significant bits, and inserts two zeroes in between each bit. This results in
        /// a 30 bit value (which still fits into a 32 bit uint).
        ///
        /// By ending up with a 30 bit value, we then have 2 free spaces left to "shift" the bits for interleaving later.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint ExpandBits(uint val)
        {
            val = (val * 0x00010001u) & 0xFF0000FFu;
            val = (val * 0x00000101u) & 0x0F00F00Fu;
            val = (val * 0x00000011u) & 0xC30C30C3u;
            val = (val * 0x00000005u) & 0x49249249u;
            return val;
        }
    }

    [BurstCompile]
    public unsafe struct BVHComputeMortonCodesAndDataIndexesJob : IJobFor
    {
        public int WorkerCount;
        [ReadOnly]
        public NativeReference<AABB> SceneAABB;
        [NativeDisableParallelForRestriction]
        public NativeList<BVHNode> UnsortedNodes;
        
        public void Execute(int index)
        {
            int nodesPerWorker = MathUtilities.DivideIntCeil(UnsortedNodes.Length, WorkerCount);
            int startIndex = index * nodesPerWorker;
            
            // Compute morton codes (from normalized position relative to scene AABB)
            AABB sceneAABB = SceneAABB.Value;
            float3 sceneDimensions = sceneAABB.Max - sceneAABB.Min;
            BVHNode* nodesPtr = UnsortedNodes.GetUnsafePtr();
            for (int i = startIndex; i < math.min(UnsortedNodes.Length, startIndex + nodesPerWorker); i++)
            {
                ref BVHNode nodeRef = ref UnsafeUtility.ArrayElementAsRef<BVHNode>(nodesPtr, i);
                float3 normalizedPosition = (nodeRef.AABB.GetCenter() - sceneAABB.Min) / sceneDimensions; // Position from 0f to 1f in the scene
                nodeRef.MortonCode = BVHUtils.ComputeMortonCode(normalizedPosition);
                nodeRef.DataIndex = i;
            }
        }
    }

    [BurstCompile]
    public struct BVHSortNodesInitialJob : IJob
    {
        public int WorkerCount;
        [ReadOnly]
        public NativeList<BVHNode> UnsortedNodes;
        public NativeList<UnsafeList<int>> SortingNodeBuckets;
        public NativeList<UnsafeList<int>> SortingNodeIndexes;

        public void Execute()
        {
            UnsafeList<int> srcNodes = SortingNodeIndexes[0];
            UnsafeList<int> dstNodes = SortingNodeIndexes[1];
            srcNodes.Resize(UnsortedNodes.Length, NativeArrayOptions.UninitializedMemory);
            dstNodes.Resize(UnsortedNodes.Length, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < UnsortedNodes.Length; i++)
            {
                srcNodes[i] = i;
            }
            SortingNodeIndexes[0] = srcNodes;
            SortingNodeIndexes[1] = dstNodes;
            
            // Ensure buckets capacities
            int requiredBucketsCount = BVHUtils.NbValuesPerMortonCodeDigit * WorkerCount;
            if (SortingNodeBuckets.Length < requiredBucketsCount)
            {
                int lengthDiff = requiredBucketsCount - SortingNodeBuckets.Length;
                for (int i = 0; i < lengthDiff; i++)
                {
                    SortingNodeBuckets.Add(new UnsafeList<int>(256, Allocator.Persistent));
                }
            }
        }
    }

    [BurstCompile]
    public unsafe struct BVHSortNodesParallelJob : IJobFor
    {
        public int WorkerCount;
        public int DigitIndex;
        [ReadOnly]
        public NativeList<BVHNode> UnsortedNodes;
        [NativeDisableParallelForRestriction]
        public NativeList<UnsafeList<int>> SortingNodeBuckets;
        [NativeDisableParallelForRestriction]
        public NativeList<UnsafeList<int>> SortingNodeIndexes;

        public void Execute(int workerIndex)
        {
            // Powers of 10
            Span<uint> powersOfTen = stackalloc uint[]
            {
                1,
                10,
                100,
                1000,
                10000,
                100000,
                1000000,
                10000000,
                100000000,
                1000000000
            };
            
            int nodesCountForWorker = MathUtilities.DivideIntCeil(UnsortedNodes.Length, WorkerCount);
            int nodesStartIndex = workerIndex * nodesCountForWorker;
            int nodesEndIndex = math.min(UnsortedNodes.Length, nodesStartIndex + nodesCountForWorker);
            int bucketsStartIndex = workerIndex * BVHUtils.NbValuesPerMortonCodeDigit;

            // Swap dst and src nodes
            UnsafeList<int> srcNodes;
            if (DigitIndex % 2 == 0)
            {
                srcNodes = SortingNodeIndexes[0];
            }
            else
            {
                srcNodes = SortingNodeIndexes[1];
            }
            
            // Get the digit at that index for each node, and put it in the corresponding bucket
            for (int srcNodeIndex = nodesStartIndex; srcNodeIndex < nodesEndIndex; srcNodeIndex++)
            {
                uint mortonCode = UnsortedNodes[srcNodes[srcNodeIndex]].MortonCode;
                int digitValue = (int)((mortonCode / powersOfTen[DigitIndex]) % 10);
                
                UnsafeList<int> bucketForValue = SortingNodeBuckets[bucketsStartIndex + digitValue];
                bucketForValue.AddWithGrowFactor(srcNodes[srcNodeIndex], 2f);
                SortingNodeBuckets[bucketsStartIndex + digitValue] = bucketForValue;
            }
        }
    }

    [BurstCompile]
    public unsafe struct BVHSortNodesMergeJob : IJob
    {
        public int WorkerCount;
        public int DigitIndex;
        public NativeList<UnsafeList<int>> SortingNodeBuckets;
        public NativeList<UnsafeList<int>> SortingNodeIndexes;

        public void Execute()
        {
            UnsafeList<int> dstNodes;
            if (DigitIndex % 2 == 0)
            {
                dstNodes = SortingNodeIndexes[1];
            }
            else
            {
                dstNodes = SortingNodeIndexes[0];
            }
                
            // Update sorted nodes from buckets
            int* dstPtr = dstNodes.Ptr;
            for (int bucketValue = 0; bucketValue < BVHUtils.NbValuesPerMortonCodeDigit; bucketValue++)
            {
                for (int workerIndex = 0; workerIndex < WorkerCount; workerIndex++)
                {
                    int workerBucketIndex = (workerIndex * BVHUtils.NbValuesPerMortonCodeDigit) + bucketValue;
                    
                    UnsafeList<int> bucketList = SortingNodeBuckets[workerBucketIndex];
                    UnsafeUtility.MemCpy(dstPtr, bucketList.Ptr, UnsafeUtility.SizeOf<int>() * bucketList.Length);
                    dstPtr = dstPtr + (long)bucketList.Length;
                    bucketList.Clear();
                    SortingNodeBuckets[workerBucketIndex] = bucketList;
                }
            }
        }
    }

    [BurstCompile]
    public struct BVHSortNodesFinalJob : IJob
    {
        public NativeList<BVHNode> UnsortedNodes;
        public NativeList<BVHNode> SortedNodes;
        public NativeList<UnsafeList<int>> SortingNodeIndexes;

        public void Execute()
        {
            SortedNodes.Resize(UnsortedNodes.Length, NativeArrayOptions.UninitializedMemory);
            UnsafeList<int> dstNodes = SortingNodeIndexes[0];
            
            // Process final sorted results
            for (int i = 0; i < dstNodes.Length; i++)
            {
                SortedNodes[i] = UnsortedNodes[dstNodes[i]];
            }
        }
    }

    [BurstCompile]
    public unsafe struct BVHSingleSortJob : IJob
    {
        public NativeList<BVHNode> UnsortedNodes;
        public NativeList<BVHNode> SortedNodes;

        public void Execute()
        {
            if (SortedNodes.Length != UnsortedNodes.Length)
            {
                SortedNodes.Resize(UnsortedNodes.Length, NativeArrayOptions.ClearMemory);
            } 
            
            // Swap lists
            NativeList<BVHNode> tmpUnsortedNodes = UnsortedNodes;
            SortedNodes = UnsortedNodes;
            UnsortedNodes = tmpUnsortedNodes;
            
            // Sort
            SortedNodes.Sort();
        }
    }

    [BurstCompile]
    public struct BVHBuildHierarchyJob : IJob
    {
        public NativeList<BVHNode> SortedNodes;
        // TODO: mostly for debug. Keep it?
        public NativeList<StartIndexAndCount> NodeLevelStartIndexesAndCounts;
        
        public void Execute()
        {
            if(SortedNodes.Length < 2)
                return;
            
            // If nodes count is not even, add padding node
            if (SortedNodes.Length % 2 != 0)
            {
                SortedNodes.Add(new BVHNode
                {
                    DataIndex = -1,
                    AABB = SortedNodes[SortedNodes.Length - 1].AABB,
                });
            }
            
            // Build node hierarchy
            int nodesStartForLevel = 0;
            int nodesCountForLevel = SortedNodes.Length;
            while (nodesCountForLevel > 1)
            {
                NodeLevelStartIndexesAndCounts.Add(new StartIndexAndCount
                {
                    StartIndex = nodesStartForLevel,
                    Count = nodesCountForLevel,
                });
                
                int nodesLengthBeforeAdd = SortedNodes.Length;
                
                for (int i = nodesStartForLevel; i < nodesLengthBeforeAdd; i += 2)
                {
                    AABB aabb = SortedNodes[i].AABB;
                    aabb.Include(SortedNodes[i + 1].AABB);

                    SortedNodes.Add(new BVHNode
                    {
                        AABB = aabb,
                        DataIndex = i,
                    });
                }
            
                nodesStartForLevel = nodesLengthBeforeAdd;
                nodesCountForLevel = SortedNodes.Length - nodesLengthBeforeAdd;
                
                // If nodes count is not even amd is not root node, add padding node
                if (nodesCountForLevel > 1 && nodesCountForLevel % 2 != 0)
                {
                    SortedNodes.Add(new BVHNode
                    {
                        DataIndex = -1,
                        AABB = SortedNodes[SortedNodes.Length - 1].AABB,
                    });
                    nodesCountForLevel += 1;
                }
            }
            
            // Add final level
            NodeLevelStartIndexesAndCounts.Add(new StartIndexAndCount
            {
                StartIndex = nodesStartForLevel,
                Count = nodesCountForLevel,
            });
        }
    }
}