using Trove.SpatialQueries;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using AABB = Trove.AABB;

public struct SpatialQueryTester : IComponentData
{
    public Entity BVHCubePrefab;

    public int SpawnCount;
    public AABB SpawnArea;
    public float SpawnScale;

    public bool IsInitialized;
}

partial struct SpatialQueryTesterSystem : ISystem
{
    public struct TestNodeData
    {
        public Entity Entity;
    }
    
    private BVH<TestNodeData> _bvh;
    
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _bvh = BVH<TestNodeData>.Create(Allocator.Domain, 100000);
    }

    [BurstCompile]
    public void OnDestroy(ref SystemState state)
    {
        
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (tester, entity) in SystemAPI.Query<RefRW<SpatialQueryTester>>().WithEntityAccess())
        {
            if (!tester.ValueRW.IsInitialized)
            {
                Unity.Mathematics.Random random = Unity.Mathematics.Random.CreateFromIndex(0);
                for (int i = 0; i < tester.ValueRW.SpawnCount; i++)
                {
                    Entity newInstance = ecb.Instantiate(tester.ValueRW.BVHCubePrefab);
                    ecb.SetComponent(newInstance, LocalTransform.FromPositionRotationScale(
                        random.NextFloat3(tester.ValueRW.SpawnArea.Min, tester.ValueRW.SpawnArea.Max),
                        random.NextQuaternionRotation(),
                        tester.ValueRO.SpawnScale));
                }
                
                tester.ValueRW.IsInitialized = true;
            }
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
        
        // state.Dependency = new QueryBVHRecursiveJob()
        // {
        //     BVH = _bvh,
        // }.ScheduleParallel(state.Dependency);
        //
        // state.Dependency = new QueryBVHStackJob()
        // {
        //     BVH = _bvh,
        // }.ScheduleParallel(state.Dependency);
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
        [ReadOnly]
        public BVH<TestNodeData> BVH;
        
        private UnsafeList<TestNodeData> results;
        
        public void Execute(in LocalTransform transform, ref BVHTestObject test)
        {
            results.Clear();
            AABB aabb = AABB.FromCenterExtents(transform.Position, test.AABBExtents * transform.Scale * 5f);
            
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
        [ReadOnly]
        public BVH<TestNodeData> BVH;
        
        private UnsafeList<TestNodeData> results;
        private UnsafeList<int> workStack;
        
        public void Execute(in LocalTransform transform, ref BVHTestObject test)
        {
            results.Clear();
            AABB aabb = AABB.FromCenterExtents(transform.Position, test.AABBExtents * 10f);
            
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
