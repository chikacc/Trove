using Unity.Entities;
using UnityEngine;
using Trove.SpatialQueries;
using Unity.Mathematics;
using AABB = Trove.AABB;

public struct SpatialQueryTester : IComponentData
{
    public Entity BVHCubePrefab;

    public bool UseParallelAdd;
    public bool UseParallelSort;
    public bool UseParallelBuild;

    public int SpawnCount;
    public AABB SpawnArea;
    public float SpawnScale;
    
    public float QuerierRatio;
    public float QueryScale;

    public bool IsInitialized;
}

class SpatialQueryTesterAuthoring : MonoBehaviour
{
    public GameObject BVHCubePrefab;

    public bool UseParallelAdd;
    public bool UseParallelSort;
    public bool UseParallelBuild;
    
    public int SpawnCount = 100;
    public float SpawnScale = 1f;
    public float3 SpawnAreaCenter = float3.zero;
    public float3 SpawnAreaExtents = new float3(50f);

    public float QuerierRatio = 1f;
    public float QueryScale = 4f;
}

class SpatialQueryTesterAuthoringBaker : Baker<SpatialQueryTesterAuthoring>
{
    public override void Bake(SpatialQueryTesterAuthoring authoring)
    {
        Entity entity = GetEntity(authoring, TransformUsageFlags.Dynamic);
        AddComponent(entity, new SpatialQueryTester
        {
            BVHCubePrefab = GetEntity(authoring.BVHCubePrefab, TransformUsageFlags.None),
            
            UseParallelAdd = authoring.UseParallelAdd,
            UseParallelSort = authoring.UseParallelSort,
            UseParallelBuild = authoring.UseParallelBuild,
            
            QuerierRatio = authoring.QuerierRatio,
            QueryScale = authoring.QueryScale,
            
            SpawnCount = authoring.SpawnCount,
            SpawnScale = authoring.SpawnScale,
            SpawnArea = AABB.FromCenterExtents(authoring.SpawnAreaCenter, authoring.SpawnAreaExtents),
        });
    }
}
