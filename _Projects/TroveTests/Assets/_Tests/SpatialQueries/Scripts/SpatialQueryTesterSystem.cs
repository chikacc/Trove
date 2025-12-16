using Trove.DebugDraw;
using Trove.SpatialQueries;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using AABB = Trove.AABB;

public struct SpatialQueryTester : IComponentData
{
    public Entity BVHCubePrefab;

    public int SpawnCount;
    public AABB SpawnArea;
    public float SpawnScale;
    
    public float QuerierRatio;
    public float QueryScale;

    public bool IsInitialized;
}

partial struct SpatialQueryTesterSystem : ISystem
{
    private DebugDrawGroup _debugDrawGroup;
    
    public struct TestNodeData
    {
        public Entity Entity;
    }
    
    private BVH<TestNodeData> _bvh;
    
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _bvh = BVH<TestNodeData>.Create(Allocator.Domain, 100000);
        
        state.RequireForUpdate<SpatialQueryTester>();
        state.RequireForUpdate<DebugDrawSingleton>();
    }

    [BurstCompile]
    public void OnDestroy(ref SystemState state)
    {
        
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // Allocate the DebugDrawGroup if not created
        if (!_debugDrawGroup.IsCreated)
        {
            ref DebugDrawSingleton debugDrawSingleton = ref SystemAPI.GetSingletonRW<DebugDrawSingleton>().ValueRW;
            _debugDrawGroup = debugDrawSingleton.AllocateDebugDrawGroup();
        }
        
        if (SystemAPI.HasSingleton<SpatialQueryTester>())
        {
            ref SpatialQueryTester tester = ref SystemAPI.GetSingletonRW<SpatialQueryTester>().ValueRW;
            
            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);

            if (!tester.IsInitialized)
            {
                Unity.Mathematics.Random random = Unity.Mathematics.Random.CreateFromIndex(0);
                for (int i = 0; i < tester.SpawnCount; i++)
                {
                    Entity newInstance = ecb.Instantiate(tester.BVHCubePrefab);
                    ecb.SetComponent(newInstance, LocalTransform.FromPositionRotationScale(
                        random.NextFloat3(tester.SpawnArea.Min, tester.SpawnArea.Max),
                        random.NextQuaternionRotation(),
                        tester.SpawnScale));
                }

                tester.IsInitialized = true;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            state.Dependency = new ClearBVHJob
            {
                BVH = _bvh,
            }.Schedule(state.Dependency);

            state.Dependency = new AddToBVHJob
            {
                BVH = _bvh,
            }.Schedule(state.Dependency);

            state.Dependency = new BuildBVHMortonJob()
            {
                BVH = _bvh,
            }.Schedule(state.Dependency);

            state.Dependency = new BuildBVHSortJob
            {
                BVH = _bvh,
            }.Schedule(state.Dependency);

            state.Dependency = new BuildBVHHierarchyJob
            {
                BVH = _bvh,
            }.Schedule(state.Dependency);

            // state.Dependency = new BuildBVHJob
            // {
            //     BVH = _bvh,
            // }.Schedule(state.Dependency);

            state.Dependency = new QueryBVHRecursiveJob()
            {
                QueryScale = tester.QueryScale,
                BVH = _bvh,
            }.ScheduleParallel(state.Dependency);

            state.Dependency = new QueryBVHStackJob()
            {
                QueryScale = tester.QueryScale,
                BVH = _bvh,
            }.ScheduleParallel(state.Dependency);

            // Debug
            if (SystemAPI.HasSingleton<BVHDebugger>())
            {
                BVHDebugger debugger = SystemAPI.GetSingleton<BVHDebugger>();
                
                _debugDrawGroup.Clear();
                state.Dependency.Complete();

                if (debugger.DebugMortonCurve)
                {
                    _bvh.GetNodes(out UnsafeList<BVHNode> nodes, 
                        out UnsafeList<StartIndexAndCount> levelStartIndexesAndCounts);

                    if (levelStartIndexesAndCounts.Length > 0)
                    {
                        StartIndexAndCount leafNodesData = levelStartIndexesAndCounts[0];

                        for (int i = leafNodesData.StartIndex; 
                             i < math.min(debugger.MortonCurveDebugIterations, leafNodesData.StartIndex + leafNodesData.Count - 1); 
                             i++)
                        {
                            BVHNode node = nodes[i];
                            BVHNode nextNode = nodes[i + 1];
                            _debugDrawGroup.DrawLine(node.AABB.GetCenter(), nextNode.AABB.GetCenter(), UnityEngine.Color.yellow);
                        }
                    }
                }

                if (debugger.DebugBoundingBoxes)
                {
                    _bvh.GetNodes(out UnsafeList<BVHNode> nodes, 
                        out UnsafeList<StartIndexAndCount> levelStartIndexesAndCounts);

                    if (debugger.BoundingBoxDebugLevel >= 0 && levelStartIndexesAndCounts.Length > debugger.BoundingBoxDebugLevel)
                    {
                        StartIndexAndCount levelNodesData = levelStartIndexesAndCounts[debugger.BoundingBoxDebugLevel];
                        for (int i = levelNodesData.StartIndex; i < levelNodesData.StartIndex + levelNodesData.Count - 1; i++)
                        {
                            BVHNode node = nodes[i];
                            _debugDrawGroup.DrawWireBox(
                                node.AABB.GetCenter(), 
                                quaternion.identity,
                                node.AABB.GetExtents(), 
                                UnityEngine.Color.green);
                        }
                    }
                }

                if (debugger.QueryEnabled)
                {
                    ComponentLookup<LocalTransform> localTransformLookup = state.GetComponentLookup<LocalTransform>(true);
                    ComponentLookup<BVHTestObject> bvhTestObjectLookup = state.GetComponentLookup<BVHTestObject>(true);
                    
                    UnsafeList<TestNodeData> results = new UnsafeList<TestNodeData>(2000, Allocator.Temp);
                    UnsafeList<int> workStack = new UnsafeList<int>(5000, Allocator.Temp);

                    results.Clear();
                    AABB aabb = AABB.FromCenterExtents(debugger.QueryPosition,
                        debugger.QueryExtents);

                    _bvh.QueryAABBStack(aabb, ref workStack, ref results);
                    
                    // Draw query bounds
                    _debugDrawGroup.DrawWireBox(
                        debugger.QueryPosition, 
                        quaternion.identity,
                        debugger.QueryExtents, 
                        UnityEngine.Color.blue);
                    
                    // Draw query results
                    for (int i = 0; i < results.Length; i++)
                    {
                        Entity resultEntity = results[i].Entity;
                        if (localTransformLookup.TryGetComponent(resultEntity, out LocalTransform resultTransform) &&
                            bvhTestObjectLookup.TryGetComponent(resultEntity, out BVHTestObject resultBVHTestObject))
                        {
                            _debugDrawGroup.DrawWireBox(
                                resultTransform.Position, 
                                quaternion.identity,
                                resultTransform.Scale * resultBVHTestObject.AABBExtents, 
                                UnityEngine.Color.red);
                        }
                    }

                    results.Dispose();
                    workStack.Dispose();
                }
            }
        }
    }
    
    [BurstCompile]
    public struct ClearBVHJob : IJob
    {
        public BVH<TestNodeData> BVH;
        
        public void Execute()
        {
            BVH.Clear();
        }
    }

    [BurstCompile]
    public partial struct AddToBVHJob : IJobEntity
    {
        public BVH<TestNodeData> BVH;
        
        public void Execute(Entity entity, in LocalTransform transform, in BVHTestObject test)
        {
            AABB aabb = AABB.FromCenterExtents(transform.Position, test.AABBExtents * transform.Scale);
            BVH.Add(new TestNodeData { Entity = entity }, aabb);
        }
    }
    
    [BurstCompile]
    public struct BuildBVHMortonJob : IJob
    {
        public BVH<TestNodeData> BVH;
        
        public void Execute()
        {
            BVH.Build_Mortons();
        }
    }
    
    [BurstCompile]
    public struct BuildBVHSortJob : IJob
    {
        public BVH<TestNodeData> BVH;
        
        public void Execute()
        {
            BVH.Build_Sort();
        }
    }
    
    [BurstCompile]
    public struct BuildBVHHierarchyJob : IJob
    {
        public BVH<TestNodeData> BVH;
        
        public void Execute()
        {
            BVH.Build_Hierarchy();
        }
    }
    
    [BurstCompile]
    public struct BuildBVHJob : IJob
    {
        public BVH<TestNodeData> BVH;
        
        public void Execute()
        {
            BVH.Build();
        }
    }

    [BurstCompile]
    public partial struct QueryBVHRecursiveJob : IJobEntity, IJobEntityChunkBeginEnd
    {
        public float QueryScale;
        [ReadOnly]
        public BVH<TestNodeData> BVH;
        
        private UnsafeList<TestNodeData> results;
        
        public void Execute(Entity entity, in LocalTransform transform, ref BVHTestObject test)
        {
            results.Clear();
            AABB aabb = AABB.FromCenterExtents(transform.Position, test.AABBExtents * transform.Scale * QueryScale);
            
            BVH.QueryAABBRecursive(aabb, ref results);
            
            test.QueryResultsRecursive = results.Length;
        }

        public bool OnChunkBegin(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (!results.IsCreated)
            {
                results = new UnsafeList<TestNodeData>(2000, Allocator.Temp);
            }
            
            return true;
        }

        public void OnChunkEnd(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask,
            bool chunkWasExecuted)
        {
        }
    }

    [BurstCompile]
    public partial struct QueryBVHStackJob : IJobEntity, IJobEntityChunkBeginEnd
    {
        public float QueryScale;
        [ReadOnly]
        public BVH<TestNodeData> BVH;
        
        private UnsafeList<TestNodeData> results;
        private UnsafeList<int> workStack;
        
        public void Execute(in LocalTransform transform, ref BVHTestObject test)
        {
            results.Clear();
            AABB aabb = AABB.FromCenterExtents(transform.Position, test.AABBExtents * transform.Scale * QueryScale);
            
            BVH.QueryAABBStack(aabb, ref workStack, ref results);
            
            test.QueryResultsStack = results.Length;
        }

        public bool OnChunkBegin(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (!results.IsCreated)
            {
                results = new UnsafeList<TestNodeData>(2000, Allocator.Temp);
            }
            if (!workStack.IsCreated)
            {
                workStack = new UnsafeList<int>(5000, Allocator.Temp);
            }
            
            return true;
        }

        public void OnChunkEnd(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask,
            bool chunkWasExecuted)
        {
        }
    }
}
