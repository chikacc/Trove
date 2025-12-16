using Unity.Entities;
using UnityEngine;

public struct BVHDebugger : IComponentData
{
    public bool LogMortonCodes;
    public bool DebugMortonCurve;
    public int MortonCurveDebugIterations;
    
    public bool DebugBoundingBoxes;
    public int BoundingBoxDebugLevel;
}

class BVHDebuggerBehaviour : MonoBehaviour
{
    public bool LogMortonCodes;
    public bool DebugMortonCurve;
    public int MortonCurveDebugIterations = int.MaxValue;
    
    public bool DebugBoundingBoxes;
    public int BoundingBoxDebugLevel;

    private World World;
    private Entity Entity;
    
    void Start()
    {
        World = World.DefaultGameObjectInjectionWorld;
        Entity = World.EntityManager.CreateEntity();
        World.EntityManager.AddComponentData(Entity, new BVHDebugger());
    }

    void Update()
    {
        World.EntityManager.SetComponentData(Entity, new BVHDebugger
        {
            LogMortonCodes = LogMortonCodes,
            DebugMortonCurve = DebugMortonCurve,
            MortonCurveDebugIterations = MortonCurveDebugIterations,
            
            DebugBoundingBoxes = DebugBoundingBoxes,
            BoundingBoxDebugLevel = BoundingBoxDebugLevel,
        });
    }
}
