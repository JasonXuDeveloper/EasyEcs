using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.UnitTest.Components;

namespace EasyEcs.UnitTest.Systems;

public class ResizeSystem : SystemBase
{
    public override ValueTask Execute(Context context)
    {
        // Get all entities that have both ScaleComponent and SizeComponent
        var candidates = context
            .GroupOf(typeof(ScaleComponent),
                typeof(SizeComponent));

        // Iterate over all entities
        foreach (var entity in candidates)
        {
            // Get a ref to the SizeComponent, since we will modify it
            ref var sizeComponent = ref entity.GetComponent<SizeComponent>();
            // Get the ScaleComponent's factor
            var factor = entity.GetComponent<ScaleComponent>().Factor;
            
            // Resize the size component
            sizeComponent.Width = (int) (sizeComponent.Width * factor);
            sizeComponent.Height = (int) (sizeComponent.Height * factor);
            
            // Remove ScaleComponent from the entity
            entity.RemoveComponent<ScaleComponent>();
        }
        
        return ValueTask.CompletedTask;
    }
}