
# How it Works - BVH

## Quick start template

The easiest way to get started is with the BVH template. After importing the package, right click in the Project window and select `Create > Trove > SpatialQueries > New BVH`. When creating this file from template, you should name the file knowing that the name will be used for naming various structs included in the template. For example, if you name the file `MyBVH`, the template will include these structs:
* `MyBVHNodeData`
* `MyBVHSingleton`
* `MyBVHElement`
* `MyBVHBuildSystem`

We will continue explaning assuming we named our file `MyBVH`

This gives you a `MyBVHBuildSystem` that takes care of:
* Creating a BVH on start and putting it in a `MyBVHSingleton` singleton component for easy access outside of this system.
* Updating the BVH:
    * First, clear the BVH using `_bvh.ScheduleClearJob`.
    * Then, add all entities that have a `LocalTransform` and a `MyBVHElement` component to the BVH, using the `ReserveBVHForAddJob` and `AddToBVHParallelJob` jobs.
    * Then, run some jobs that are needed after we try to parallel add to the BVH, using `_bvh.SchedulePostAddNodeUnsafeJobs`.
    * Then, build the BVH using `_bvh.ScheduleBuildJobs()`.

NOTE: if you choose to modify this, just remember that all adding of nodes to the BVH must happen after `_bvh.ScheduleClearJob` and before `_bvh.SchedulePostAddNodeUnsafeJobs`.

Simply add a `MyBVHElement` to your entities in order to make them added to the BVH and queryable automatically. If you wish to change the way in which these entities are added to the BVH (how their AABB is calculated, what extra data is stored in the BVH query results, etc...), you can modify the `AddToBVHParallelJob` job and the `MyBVHNodeData`.

Look at the comments in the template for further info.


## Querying

To query the BVH:
* Get the BVH from its singleton entity (created in the BVH template)
* Create a BVH querier using `BVH.CreateQuerier()`
* Make queries with the querier (ex: `_querier.QueryAABB(aabb, out UnsafeList<MyBVHNodeData> results);`)

NOTE: a BVH querier is a temporary struct that relies on Temp allocations. It shouldn't be created on the main thread and then passed to a job. Instead, either create and use it directly on the main thread, or create and use it in a job. The following code is an example of a `IJobEntity` that uses the Querier to make queries:

```cs
[BurstCompile]
public partial struct QueryBVHJob : IJobEntity, IJobEntityChunkBeginEnd
{
    public float QueryScale;
    [ReadOnly]
    public BVH<TestNodeData> BVH;
    
    private BVH<TestNodeData>.Querier _querier;
    
    public void Execute(in LocalTransform transform, ref BVHTestObject test)
    {
        AABB aabb = AABB.FromCenterExtents(transform.Position, test.AABBExtents * transform.Scale * QueryScale);
        _querier.QueryAABB(aabb, out UnsafeList<TestNodeData> results);
        test.QueryResultsStack = results.Length;
    }

    public bool OnChunkBegin(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        if (!_querier.IsCreated)
        {
            _querier = BVH.CreateQuerier();
        }
        
        return true;
    }

    public void OnChunkEnd(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask,
        bool chunkWasExecuted)
    {
    }
}
```
