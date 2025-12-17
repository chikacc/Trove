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
using UnityEngine.Assertions;

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

    internal struct WorkerLoadBalancingData
    {
        public int ElementsStartIndex;
        public int ElementsCount;
        public uint MinValue;
        public uint MaxValue;
    }

    public struct BVH<TNodeData> where TNodeData : unmanaged
    {
        internal NativeList<BVHNode> UnsortedNodes;
        internal NativeList<BVHNode> SortedNodes;
        internal NativeList<TNodeData> LeafNodeDatas;
        internal NativeList<StartIndexAndCount> NodeLevelStartIndexesAndCounts;
        internal NativeReference<AABB> SceneAABB;
        internal NativeList<int> NodesHistogram;
        internal NativeList<WorkerLoadBalancingData> WorkerLoadBalancingDatas;
        
        /// <summary>
        /// The Querier relies on tmp allocations, so must be used right after creation (it can't be passed between
        /// main thread and jobs, but it CAN be created and used within a job)
        /// </summary>
        public unsafe struct Querier
        {
            private UnsafeList<BVHNode> SortedNodes;
            private UnsafeList<TNodeData> LeafNodeDatas;
            private UnsafeList<int> WorkStack;
            private UnsafeList<TNodeData> Results;

            public bool IsCreated => WorkStack.IsCreated;

            internal Querier(NativeList<BVHNode> sortedNodes, NativeList<TNodeData> leafNodeDatas)
            {
                SortedNodes = *sortedNodes.GetUnsafeList();
                LeafNodeDatas = *leafNodeDatas.GetUnsafeList();
                WorkStack = new UnsafeList<int>(32, Allocator.Temp);
                Results = new UnsafeList<TNodeData>(32, Allocator.Temp);
            }
            
            /// <summary>
            /// Note: "results" is temporary and will be overwritten the next time any query is made
            /// </summary>
            public void QueryAABB(in AABB aabb, out UnsafeList<TNodeData> results)
            {
                Results.Clear();
                
                // Add root node to stack
                WorkStack.Clear();
                WorkStack.Add(SortedNodes.Length - 1);

                for (int i = 0; i < WorkStack.Length; i++)
                {
                    int nodeIndex = WorkStack[i];
                    BVHNode queriedNode = SortedNodes[nodeIndex];
                    if (queriedNode.IsValid() && aabb.OverlapAABB(queriedNode.AABB))
                    {
                        if (nodeIndex < LeafNodeDatas.Length)
                        {
                            Results.AddWithGrowFactor(LeafNodeDatas[queriedNode.DataIndex]);
                        }
                        else
                        {
                            // Add both child nodes to stack
                            WorkStack.AddWithGrowFactor(queriedNode.DataIndex);
                            WorkStack.AddWithGrowFactor(queriedNode.DataIndex + 1);
                        }
                    }
                }
                
                results = Results;
            }

            // TODO
            // public void QueryRay(in AABB aabb, out UnsafeList<TNodeData> results)
            // {
            //     Results.Clear();
            //     
            //     // Add root node to stack
            //     WorkStack.Clear();
            //     WorkStack.Add(SortedNodes.Length - 1);
            //     
            //     results = Results;
            // }
        }

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
            bvh.NodesHistogram = new NativeList<int>(BVHUtils.NodesHistogramSlicesCount, allocator);
            bvh.NodesHistogram.Resize(bvh.NodesHistogram.Capacity, NativeArrayOptions.ClearMemory);
            bvh.WorkerLoadBalancingDatas = new NativeList<WorkerLoadBalancingData>(16, allocator);
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

            if (NodesHistogram.IsCreated)
            {
                NodesHistogram.Dispose(jobHandle);
            }

            if (WorkerLoadBalancingDatas.IsCreated)
            {
                WorkerLoadBalancingDatas.Dispose(jobHandle);
            }
        }
 
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void AddNode(in TNodeData nodeData, in AABB aabb)
        {
            ref AABB sceneAABBRef = ref *SceneAABB.GetUnsafePtr();
            sceneAABBRef.Include(aabb);
            
            UnsortedNodes.Add(new BVHNode
            {
                AABB = aabb,
            });
            LeafNodeDatas.Add(nodeData);
        }

        public void ReserveAddNodesUnsafe(int addNodesCount, out int startIndexOfReservedRange)
        {
            startIndexOfReservedRange = UnsortedNodes.Length;
            UnsortedNodes.Resize(UnsortedNodes.Length + addNodesCount, NativeArrayOptions.UninitializedMemory);
            LeafNodeDatas.Resize(LeafNodeDatas.Length + addNodesCount, NativeArrayOptions.UninitializedMemory);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void AddNodeUnsafe(in TNodeData nodeData, in AABB aabb, int atIndex)
        {
            UnsortedNodes[atIndex] = new BVHNode
            {
                AABB = aabb,
            };
            LeafNodeDatas[atIndex] = nodeData;
        }

        public JobHandle ScheduleClearJob(JobHandle dep)
        {
            dep = new BVHClearJob
            {
                BVH = this,
            }.Schedule(dep);
            
            return dep;
        }

        public JobHandle SchedulePostAddNodeUnsafeJobs(JobHandle dep)
        {
            int workerCount = JobsUtility.JobWorkerCount;
            NativeArray<AABB> aabbForWorker = new NativeArray<AABB>(workerCount, Allocator.Domain);
            for (int i = 0; i < workerCount; i++)
            {
                aabbForWorker[i] = AABB.GetEmpty();
            }

            dep = new RecomputeSceneAABBsParallelJob
            {
                WorkerCount = workerCount,
                UnsortedNodes = UnsortedNodes,
                AABBForWorker = aabbForWorker,
            }.ScheduleParallel(workerCount, 1, dep);
            
            dep = new RecomputeSceneAABBsMergeJob()
            {
                SceneAABB = SceneAABB,
                AABBForWorker = aabbForWorker,
            }.Schedule(dep);
            
            aabbForWorker.Dispose(dep);
            
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
            
            if(useParallelSort)
            {
                dep = new BVHInitializeSortJob
                {
                    WorkerCount = workerCount,
                    UnsortedNodes = UnsortedNodes,
                    SortedNodes = SortedNodes,
                    NodesHistogram = NodesHistogram,
                    WorkerLoadBalancingData = WorkerLoadBalancingDatas,
                }.Schedule(dep);
                
                dep = new BVHComputeNodesHistogramJob
                {
                    WorkerCount = workerCount,
                    UnsortedNodes = UnsortedNodes,
                    NodesHistogram = NodesHistogram,
                }.ScheduleParallel(workerCount, 1, dep);
                
                dep = new BVHMergeHistogramsAndComputeLoadBalancingJob
                {
                    WorkerCount = workerCount,
                    UnsortedNodes = UnsortedNodes,
                    NodesHistogram = NodesHistogram,
                    WorkerLoadBalancingData = WorkerLoadBalancingDatas,
                }.Schedule(dep);
                
                dep = new BVHParallelSortJob()
                {
                    UnsortedNodes = UnsortedNodes,
                    SortedNodes = SortedNodes,
                    WorkerLoadBalancingData = WorkerLoadBalancingDatas,
                }.ScheduleParallel(workerCount, 1, dep);
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

        public Querier CreateQuerier()
        {
            return new Querier(SortedNodes, LeafNodeDatas);
        }
    
        [BurstCompile]
        public struct BVHClearJob : IJob
        {
            public BVH<TNodeData> BVH;
        
            public void Execute()
            {
                BVH.UnsortedNodes.Clear();
                BVH.SortedNodes.Clear();
                BVH.LeafNodeDatas.Clear();
                BVH.NodeLevelStartIndexesAndCounts.Clear();
                BVH.SceneAABB.Value = AABB.GetEmpty();
            }
        }
    }

    internal static class BVHUtils
    {
        internal const int NodesHistogramSlicesCount = 4000;
        internal const uint ValuesPerNodeHistogramSlice = (uint.MaxValue / NodesHistogramSlicesCount) + 1;
        
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
    public unsafe struct RecomputeSceneAABBsParallelJob : IJobFor
    {
        public int WorkerCount;
        [ReadOnly]
        public NativeList<BVHNode> UnsortedNodes;
        [NativeDisableParallelForRestriction]
        public NativeArray<AABB> AABBForWorker;
        
        public void Execute(int workerIndex)
        {
            int nodesPerWorker = MathUtilities.DivideIntCeil(UnsortedNodes.Length, WorkerCount);
            int startIndex = workerIndex * nodesPerWorker;
            int endIndex = math.min(UnsortedNodes.Length, startIndex + nodesPerWorker);

            ref AABB aabbForWorker = ref UnsafeUtility.ArrayElementAsRef<AABB>(AABBForWorker.GetUnsafePtr(), workerIndex);
            
            for (int i = startIndex; i < endIndex; i++)
            {
                aabbForWorker.Include(UnsortedNodes[i].AABB);
            }
        }
    }

    [BurstCompile]
    public unsafe struct RecomputeSceneAABBsMergeJob : IJob
    {
        public NativeReference<AABB> SceneAABB;
        public NativeArray<AABB> AABBForWorker;
        
        public void Execute()
        {
            ref AABB sceneAABBRef = ref *SceneAABB.GetUnsafePtr();
            
            for (int i = 0; i < AABBForWorker.Length; i++)
            {
                sceneAABBRef.Include(AABBForWorker[i]);
            }
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
            int endIndex = math.min(UnsortedNodes.Length, startIndex + nodesPerWorker);
            
            // Compute morton codes (from normalized position relative to scene AABB)
            AABB sceneAABB = SceneAABB.Value;
            float3 sceneDimensions = sceneAABB.Max - sceneAABB.Min;
            BVHNode* nodesPtr = UnsortedNodes.GetUnsafePtr();
            for (int i = startIndex; i < endIndex; i++)
            {
                ref BVHNode nodeRef = ref UnsafeUtility.ArrayElementAsRef<BVHNode>(nodesPtr, i);
                float3 normalizedPosition = (nodeRef.AABB.GetCenter() - sceneAABB.Min) / sceneDimensions; // Position from 0f to 1f in the scene
                nodeRef.MortonCode = BVHUtils.ComputeMortonCode(normalizedPosition);
                nodeRef.DataIndex = i;
            }
        }
    }

    [BurstCompile]
    internal struct BVHInitializeSortJob : IJob
    {
        public int WorkerCount;
        public NativeList<BVHNode> UnsortedNodes;
        public NativeList<BVHNode> SortedNodes;
        public NativeList<int> NodesHistogram;
        public NativeList<WorkerLoadBalancingData> WorkerLoadBalancingData;

        public void Execute()
        {
            // Ensure sorted nodes length
            if (SortedNodes.Length != UnsortedNodes.Length)
            {
                SortedNodes.Resize(UnsortedNodes.Length, NativeArrayOptions.UninitializedMemory);
            }

            // Ensure histogram length
            if (NodesHistogram.Length != BVHUtils.NodesHistogramSlicesCount * WorkerCount)
            {
                NodesHistogram.Resize(BVHUtils.NodesHistogramSlicesCount * WorkerCount, NativeArrayOptions.ClearMemory);
            }
            
            // Clear histogram
            for (int i = 0; i < NodesHistogram.Length; i++)
            {
                NodesHistogram[i] = 0;
            }
            
            // Clear worker slice ranges
            WorkerLoadBalancingData.Clear();
        }
    }

    [BurstCompile]
    internal unsafe struct BVHComputeNodesHistogramJob : IJobFor
    {
        public int WorkerCount;
        [ReadOnly]
        public NativeList<BVHNode> UnsortedNodes;
        [NativeDisableParallelForRestriction]
        public NativeList<int> NodesHistogram;

        public void Execute(int workerIndex)
        {
            int nodesPerWorker = MathUtilities.DivideIntCeil(UnsortedNodes.Length, WorkerCount);
            int startIndex = workerIndex * nodesPerWorker;
            int endIndex = math.min(UnsortedNodes.Length, startIndex + nodesPerWorker);

            int* nodesHistogramPtrForWorker = NodesHistogram.GetUnsafePtr() + (long)(BVHUtils.NodesHistogramSlicesCount * workerIndex);
            
            // For each node in this worker's range, 
            for (int i = startIndex; i < endIndex; i++)
            {
                uint mortonCode = UnsortedNodes[i].MortonCode;
                int histogramSliceIndex = (int)(mortonCode / BVHUtils.ValuesPerNodeHistogramSlice);
                ref int histogramValRef = ref UnsafeUtility.ArrayElementAsRef<int>(nodesHistogramPtrForWorker, histogramSliceIndex);
                histogramValRef++;
            }
        }
    }

    [BurstCompile]
    internal unsafe struct BVHMergeHistogramsAndComputeLoadBalancingJob : IJob
    {
        public int WorkerCount;
        public NativeList<BVHNode> UnsortedNodes;
        public NativeList<int> NodesHistogram;
        public NativeList<WorkerLoadBalancingData> WorkerLoadBalancingData;

        public void Execute()
        {
            int* nodesHistogramPtr = NodesHistogram.GetUnsafePtr();
            
            // Merge all into the first worker's range
            for (int workerIndex = 1; workerIndex < WorkerCount; workerIndex++)
            {
                for (int sliceIndex = 0; sliceIndex < BVHUtils.NodesHistogramSlicesCount; sliceIndex++)
                {
                    ref int firstWorkerHistogramValRef = ref UnsafeUtility.ArrayElementAsRef<int>(nodesHistogramPtr, sliceIndex);
                    firstWorkerHistogramValRef += NodesHistogram[(workerIndex * BVHUtils.NodesHistogramSlicesCount) + sliceIndex];
                }
            }
            
            // Compute load balancing
            // Assign a slices range to each worker so that each worker has a similar quantity of nodes to process
            int totalElementsCounter = 0;
            int desiredElementsPerWorker = MathUtilities.DivideIntCeil(UnsortedNodes.Length, WorkerCount);
            WorkerLoadBalancingData tmpLoadBalancingData = new WorkerLoadBalancingData();
            for (int i = 0; i < BVHUtils.NodesHistogramSlicesCount; i++)
            {
                tmpLoadBalancingData.MaxValue += BVHUtils.ValuesPerNodeHistogramSlice;
                tmpLoadBalancingData.ElementsCount += NodesHistogram[i];
                totalElementsCounter += NodesHistogram[i];
                
                if (tmpLoadBalancingData.ElementsCount >= desiredElementsPerWorker ||
                    i >= BVHUtils.NodesHistogramSlicesCount - 1)
                {
                    uint addedMaxValue = tmpLoadBalancingData.MaxValue;
                    WorkerLoadBalancingData.Add(tmpLoadBalancingData);
                    
                    tmpLoadBalancingData = new WorkerLoadBalancingData
                    {
                        ElementsStartIndex = totalElementsCounter,
                        ElementsCount = 0,
                        MinValue = addedMaxValue,
                        MaxValue = addedMaxValue,
                    };
                }
            }

            Assert.IsTrue(WorkerLoadBalancingData.Length <= WorkerCount);
        }
    }

    [BurstCompile]
    internal unsafe struct BVHParallelSortJob : IJobFor
    {
        [ReadOnly]
        public NativeList<BVHNode> UnsortedNodes;
        [NativeDisableParallelForRestriction]
        public NativeList<BVHNode> SortedNodes;
        [ReadOnly]
        public NativeList<WorkerLoadBalancingData> WorkerLoadBalancingData;

        public void Execute(int workerIndex)
        {
            if (workerIndex < WorkerLoadBalancingData.Length)
            {
                WorkerLoadBalancingData loadBalancingData = WorkerLoadBalancingData[workerIndex];

                // First pass; group nodes by value range contiguously in sorted array
                int addedCounter = 0;
                for (int i = 0; i < UnsortedNodes.Length; i++)
                {
                    BVHNode node = UnsortedNodes[i];
                    if (node.MortonCode >= loadBalancingData.MinValue && node.MortonCode < loadBalancingData.MaxValue)
                    {
                        SortedNodes[loadBalancingData.ElementsStartIndex + addedCounter] = node;
                        addedCounter++;
                    }
                }

                // Second pass; sort grouped nodes of value range
                UnsafeList<BVHNode> sortingNodes = new UnsafeList<BVHNode>(
                    SortedNodes.GetUnsafePtr() + (long)loadBalancingData.ElementsStartIndex,
                    loadBalancingData.ElementsCount);
                sortingNodes.Sort();
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
    public struct BVHPrecomputeHierarchyJob : IJob
    {
        public NativeList<BVHNode> SortedNodes;
        public NativeList<StartIndexAndCount> NodeLevelStartIndexesAndCounts;

        public void Execute()
        {
            // 
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