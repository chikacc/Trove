
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
* Make queries with the query methods (ex: `BVH.QueryAABB();`, `BVH.QueryRay();`)

he following code is an example of a `IJobEntity` that makes BVH queries:

```cs
[BurstCompile]
public partial struct QueryBVHJob : IJobEntity, IJobEntityChunkBeginEnd
{
    // We use the BVH for queries, so it can be ReadOnly
    [ReadOnly]
    public BVH<MyBVHNodeData> BVH;
    
    // We cache a private list of results here, reusable throughout entity iteration.
    // This way we don't have to constantly re-allocate it.
    private UnsafeList<MyBVHNodeData> results;
    
    public void Execute(in LocalTransform transform, ref MyQuerier querier)
    {
        BVH.QueryAABB(AABB.FromCenterExtents(transform.position, new float3(querier.Range)), ref UnsafeList<MyBVHNodeData> results);

        // Do what we want with the query results
        Debug.Log($"Query results count: {results.Length}");
    }

    public bool OnChunkBegin(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        // We create the results list only once per thread, and check for creation only once per chunk
        if (!results.IsCreated)
        {
            results = new UnsafeList<MyBVHNodeData>(32, Allocator.Temp);
        }
        
        return true;
    }

    public void OnChunkEnd(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask,
        bool chunkWasExecuted)
    {
    }
}
```
