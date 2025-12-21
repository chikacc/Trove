using Trove;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using AABB = Trove.AABB;

public struct BVHTestObject : IComponentData
{
    public float3 AABBExtents;

    public int QueryResults;
}

class BVHTestObjectAuthoring : MonoBehaviour
{
    public float3 AABBExtents;
}

class BVHTestObjectAuthoringBaker : Baker<BVHTestObjectAuthoring>
{
    public override void Bake(BVHTestObjectAuthoring authoring)
    {
        Entity entity = GetEntity(authoring, TransformUsageFlags.Dynamic);
        AddComponent(entity, new BVHTestObject
        {
            AABBExtents = authoring.AABBExtents,
        });
    }
}
