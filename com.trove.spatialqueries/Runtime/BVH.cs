using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace Trove.Spatial.Tests
{
    public struct BVHNode : IComparable<BVHNode>
    {
        public AABB AABB;
        public int DataIndex; // For leaf nodes this is index of their data, but for hierarchy nodes this is index of their children
        public int MortonCode; 

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsValid()
        {
            return DataIndex >= 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(BVHNode other)
        {
            return MortonCode - other.MortonCode;
        }
    }

    public struct BVH<TNodeData> where TNodeData : unmanaged
    {
        internal NativeList<BVHNode> Nodes;
        internal NativeList<TNodeData> LeafNodeDatas;

        public static BVH<TNodeData> Create(Allocator allocator, int initialElementsCapacity)
        {
            BVH<TNodeData> bvh = new BVH<TNodeData>();
            bvh.Nodes = new NativeList<BVHNode>(
                ComputeTotalNodesCountForEntries(initialElementsCapacity), 
                allocator);
            bvh.LeafNodeDatas = new NativeList<TNodeData>(
                initialElementsCapacity, allocator);
            return bvh;
        }

        private static int ComputeTotalNodesCountForEntries(int entriesCount)
        {
            float entriesCountFloat = (float)entriesCount;
            
            while (entriesCountFloat > 1f)
            {
                entriesCountFloat *= 0.5f;
                entriesCount += (int)math.ceil(entriesCountFloat);
            }
            
            return entriesCount;
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
        }

        public void Add(in TNodeData nodeData, in AABB aabb)
        {
            Nodes.Add(new BVHNode
            {
                DataIndex = LeafNodeDatas.Length,
                AABB = aabb,
            });
            LeafNodeDatas.Add(nodeData);
        }

        public unsafe void Build()
        {
            if(Nodes.Length == 0)
                return;
            
            // Compute morton codes
            for (int i = 0; i < Nodes.Length; i++)
            {
                ref BVHNode nodeRef = ref UnsafeUtility.ArrayElementAsRef<BVHNode>(Nodes.GetUnsafePtr(), i);
                nodeRef.MortonCode = GeometryUtils.ComputeMortonCode(nodeRef.AABB.GetCenter());
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
            int nodesStartForStage = 0;
            int nodesCountForStage = Nodes.Length;
            while (nodesCountForStage > 1)
            {
                for (int i = nodesStartForStage; i < nodesCountForStage; i += 2)
                {
                    AABB aabb = Nodes[i].AABB;
                    aabb.Include(Nodes[i + 1].AABB);

                    Nodes.Add(new BVHNode
                    {
                        AABB = aabb,
                        DataIndex = i,
                    });
                }
            
                nodesStartForStage = nodesCountForStage;
                nodesCountForStage = Nodes.Length - nodesCountForStage;
                
                // If nodes count is not even amd is not root node, add padding node
                if (nodesCountForStage > 1 && Nodes.Length % 2 != 0)
                {
                    Nodes.Add(new BVHNode
                    {
                        DataIndex = -1,
                        AABB = Nodes[Nodes.Length - 1].AABB,
                    });
                    nodesCountForStage += 1;
                }
            }
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
    }
}