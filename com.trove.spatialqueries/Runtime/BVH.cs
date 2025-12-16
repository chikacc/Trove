using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
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

    public struct BVH<TNodeData> where TNodeData : unmanaged
    {
        internal NativeList<BVHNode> Nodes;
        internal NativeList<TNodeData> LeafNodeDatas;
        internal NativeList<StartIndexAndCount> NodeLevelStartIndexesAndCounts;
        internal NativeReference<AABB> SceneAABB;

        public static BVH<TNodeData> Create(Allocator allocator, int initialElementsCapacity)
        {
            BVH<TNodeData> bvh = new BVH<TNodeData>();
            bvh.Nodes = new NativeList<BVHNode>(
                ComputeTotalNodesCountForEntries(initialElementsCapacity), 
                allocator);
            bvh.LeafNodeDatas = new NativeList<TNodeData>(initialElementsCapacity, allocator);
            bvh.NodeLevelStartIndexesAndCounts = new NativeList<StartIndexAndCount>(32, allocator);
            bvh.SceneAABB = new NativeReference<AABB>(Allocator.Persistent);
            return bvh;
        }

        public void Dispose(JobHandle jobHandle)
        {
            if (Nodes.IsCreated)
            {
                Nodes.Dispose(jobHandle);
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
        }

        public void EnsureElementsCapacity(int elementsCapacity)
        {
            int nodesCount = ComputeTotalNodesCountForEntries(elementsCapacity);
            if (Nodes.Capacity < nodesCount)
            {
                Nodes.SetCapacity(nodesCount);
            }

            if (LeafNodeDatas.Capacity < elementsCapacity)
            {
                LeafNodeDatas.SetCapacity(elementsCapacity);
            }
        }
        
        public void Clear()
        {
            Nodes.Clear();
            LeafNodeDatas.Clear();
            NodeLevelStartIndexesAndCounts.Clear();
            SceneAABB.Value = AABB.GetEmpty();
        }

        public void Add(in TNodeData nodeData, in AABB aabb)
        {
            AABB sceneAABB = SceneAABB.Value;
            sceneAABB.Include(aabb);
            SceneAABB.Value = sceneAABB;
            
            Nodes.Add(new BVHNode
            {
                AABB = aabb,
                DataIndex = LeafNodeDatas.Length,
            });
            LeafNodeDatas.Add(nodeData);
        }

        public unsafe void Build_Mortons()
        {
            // Compute morton codes (from normalized position relative to scene AABB)
            AABB sceneAABB = SceneAABB.Value;
            float3 sceneDimensions = sceneAABB.Max - sceneAABB.Min;
            BVHNode* nodesPtr = Nodes.GetUnsafePtr();
            for (int i = 0; i < Nodes.Length; i++)
            {
                ref BVHNode nodeRef = ref UnsafeUtility.ArrayElementAsRef<BVHNode>(nodesPtr, i);
                float3 normalizedPosition = (nodeRef.AABB.GetCenter() - sceneAABB.Min) / sceneDimensions; // Position from 0f to 1f in the scene
                nodeRef.MortonCode = ComputeMortonCode(normalizedPosition);
            }
        }

        public unsafe void Build_Sort()
        {
            // Sort by morton order
            Nodes.Sort();
        }

        public unsafe void Build_Hierarchy()
        {
            // If nodes count is not even, add padding node
            if (Nodes.Length % 2 != 0)
            {
                Nodes.Add(new BVHNode
                {
                    DataIndex = -1,
                    AABB = Nodes[Nodes.Length - 1].AABB,
                });
            }
            
            // Build node hierarchy
            int nodesStartForLevel = 0;
            int nodesCountForLevel = Nodes.Length;
            while (nodesCountForLevel > 1)
            {
                NodeLevelStartIndexesAndCounts.Add(new StartIndexAndCount
                {
                    StartIndex = nodesStartForLevel,
                    Count = nodesCountForLevel,
                });
                
                int nodesLengthBeforeAdd = Nodes.Length;
                
                for (int i = nodesStartForLevel; i < nodesLengthBeforeAdd; i += 2)
                {
                    AABB aabb = Nodes[i].AABB;
                    aabb.Include(Nodes[i + 1].AABB);

                    Nodes.Add(new BVHNode
                    {
                        AABB = aabb,
                        DataIndex = i,
                    });
                }
            
                nodesStartForLevel = nodesLengthBeforeAdd;
                nodesCountForLevel = Nodes.Length - nodesLengthBeforeAdd;
                
                // If nodes count is not even amd is not root node, add padding node
                if (nodesCountForLevel > 1 && nodesCountForLevel % 2 != 0)
                {
                    Nodes.Add(new BVHNode
                    {
                        DataIndex = -1,
                        AABB = Nodes[Nodes.Length - 1].AABB,
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

        public unsafe void Build()
        {
            if(Nodes.Length == 0)
                return;
            
            // Compute morton codes (from normalized position relative to scene AABB)
            AABB sceneAABB = SceneAABB.Value;
            float3 sceneDimensions = sceneAABB.Max - sceneAABB.Min;
            BVHNode* nodesPtr = Nodes.GetUnsafePtr();
            for (int i = 0; i < Nodes.Length; i++)
            {
                ref BVHNode nodeRef = ref UnsafeUtility.ArrayElementAsRef<BVHNode>(nodesPtr, i);
                float3 normalizedPosition = (nodeRef.AABB.GetCenter() - sceneAABB.Min) / sceneDimensions; // Position from 0f to 1f in the scene
                nodeRef.MortonCode = ComputeMortonCode(normalizedPosition);
            }
            
            // Sort by morton order
            Nodes.Sort();
            
            // If nodes count is not even, add padding node
            if (Nodes.Length % 2 != 0)
            {
                Nodes.Add(new BVHNode
                {
                    DataIndex = -1,
                    AABB = Nodes[Nodes.Length - 1].AABB,
                });
            }
            
            // Build node hierarchy
            int nodesStartForLevel = 0;
            int nodesCountForLevel = Nodes.Length;
            while (nodesCountForLevel > 1)
            {
                NodeLevelStartIndexesAndCounts.Add(new StartIndexAndCount
                {
                    StartIndex = nodesStartForLevel,
                    Count = nodesCountForLevel,
                });
                
                int nodesLengthBeforeAdd = Nodes.Length;
                
                for (int i = nodesStartForLevel; i < nodesLengthBeforeAdd; i += 2)
                {
                    AABB aabb = Nodes[i].AABB;
                    aabb.Include(Nodes[i + 1].AABB);

                    Nodes.Add(new BVHNode
                    {
                        AABB = aabb,
                        DataIndex = i,
                    });
                }
            
                nodesStartForLevel = nodesLengthBeforeAdd;
                nodesCountForLevel = Nodes.Length - nodesLengthBeforeAdd;
                
                // If nodes count is not even amd is not root node, add padding node
                if (nodesCountForLevel > 1 && nodesCountForLevel % 2 != 0)
                {
                    Nodes.Add(new BVHNode
                    {
                        DataIndex = -1,
                        AABB = Nodes[Nodes.Length - 1].AABB,
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

        public unsafe void GetNodes(out UnsafeList<BVHNode> nodes,
            out UnsafeList<StartIndexAndCount> nodeLevelStartIndexesAndCounts)
        {
            nodes = (*Nodes.GetUnsafeList());
            nodeLevelStartIndexesAndCounts = (*NodeLevelStartIndexesAndCounts.GetUnsafeList());
        }

        public void QueryAABBRecursive(in AABB aabb, ref UnsafeList<TNodeData> results)
        {
            // start at root node
            QueryAABBRecursiveInternal(Nodes.Length - 1, in aabb, ref results);
        }

        internal void QueryAABBRecursiveInternal(int nodeIndex, in AABB aabb, ref UnsafeList<TNodeData> results)
        {
            BVHNode queriedNode = Nodes[nodeIndex];
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
            workStack.Add(Nodes.Length - 1);

            for (int i = 0; i < workStack.Length; i++)
            {
                int nodeIndex = workStack[i];
                BVHNode queriedNode = Nodes[nodeIndex];
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

        private static int ComputeTotalNodesCountForEntries(int entriesCount)
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
        private static uint ComputeMortonCode(float3 nPos)
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
        private static uint ExpandBits(uint val)
        {
            val = (val * 0x00010001u) & 0xFF0000FFu;
            val = (val * 0x00000101u) & 0x0F00F00Fu;
            val = (val * 0x00000011u) & 0xC30C30C3u;
            val = (val * 0x00000005u) & 0x49249249u;
            return val;
        }
    }
}