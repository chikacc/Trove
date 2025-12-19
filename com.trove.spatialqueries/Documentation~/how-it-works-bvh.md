
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
    * Then, add all entities that have a `LocalTransform` and a `MyBVHElement` component to the BVH. There is a parallel version and a single-thread version of this in the template:
        * Single-thread: `AddToBVHJob` job.
        * Parallel: uses the `ReserveBVHForAddJob` and `AddToBVHParallelJob` jobs. Then, run some jobs that are needed after we try to parallel add to the BVH, using `_bvh.SchedulePostAddNodeUnsafeJobs`.
    * Then, build the BVH using `_bvh.ScheduleBuildJobs()`.

Simply add a `MyBVHElement` component to your entities in order to make them added to the BVH and queryable automatically. If you wish to change the way in which these entities are added to the BVH (how their AABB is calculated, what extra data is stored in the BVH query results, etc...), you can modify the `AddToBVHParallelJob` job and the `MyBVHNodeData`.

Look at the comments in the template for further info.


## Querying

To query the BVH:
* Get the BVH from its singleton entity (created in the BVH template)
* Make queries with the query methods (ex: `BVH.QueryAABB()`, `BVH.QueryRay()`, `BVH.QuerySphere()`)

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

### Querying nearest neighbors

You can also query nearest neighbors very efficiently, using a `BVH<MyBVHNodeData>NearestNeighborsQuerier`. This is a struct that allows you to iteratively query a group of close results, and expand the search at every iteration. The first iteration is guaranteed to give you at least one result.

This code sample demonstrates its usage in a job:
```cs
[BurstCompile]
public partial struct NearestNeighborsJob : IJobEntity
{
    // We use the BVH for queries, so it can be ReadOnly
    [ReadOnly]
    public BVH<MyBVHNodeData> BVH;
    
    // We cache a private list of results here, reusable throughout entity iteration.
    // This way we don't have to constantly re-allocate it.
    private UnsafeList<BVH<MyBVHNodeData>.NearestNeighborResult> results;
    
    public void Execute(in LocalTransform transform, ref MyQuerier querier)
    {
            // Find the absolute closest neighbor like this:
        if (_bvh.CreateNearestNeighborsQuerier(transform.Position, out BVH<MyBVHNodeData>.NearestNeighborsQuerier nearestNeighborsQuerier))
        {
            if(nearestNeighborsQuerier.NextResultsBatch(in _bvh, ref queryResults, true))
            {
                Debug.Log($"The closest result is {queryResults[0].Data.Entity.Index} at distance {queryResults[0].Distance}");
            }
        }
            
            // Iterate closest neighbors until we find one that meets a condition like this:
        if (_bvh.CreateNearestNeighborsQuerier(transform.Position, out nearestNeighborsQuerier))
        {
            bool conditionMet = false;
            float maxDistance = 100f;
            while(!conditionMet && nearestNeighborsQuerier.NextResultsBatch(in _bvh, ref queryResults, true))
            {
                for (int i = 0; i < queryResults.Length; i++)
                {
                    var result = queryResults[i];
                    if(result.Distance > maxDistance)
                    {
                        // We exit as soon as max distance is reached (because results are sorted by distance)
                        conditionMet = true;
                        break;
                    }
                    else if(ConditionIsMet(result))
                    {
                        Debug.Log($"{queryResults[0].Data.Entity.Index} is the closest entity that met the condition");
                        conditionMet = true;
                        break;
                    }
                }
            }
        }
    }

    public bool OnChunkBegin(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
    {
        // We create the results list only once per thread, and check for creation only once per chunk
        if (!results.IsCreated)
        {
            results = new UnsafeList<BVH<MyBVHNodeData>.NearestNeighborResult>(32, Allocator.Temp);
        }
        
        return true;
    }

    public void OnChunkEnd(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask,
        bool chunkWasExecuted)
    {
    }
}
```