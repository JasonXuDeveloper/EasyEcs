using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.UnitTest.Components;

namespace EasyEcs.UnitTest.Systems;

public class ResizeSystem : SystemBase, IInitSystem, IExecuteSystem
{
    public Task OnInit(Context context)
    {
        // Get all entities that have ScaleComponent
        using var candidates = context.GroupOf(
            typeof(ScaleComponent));
        
        // Iterate over all entities
        foreach (var entity in candidates)
        {
            // Set the factor to 2
            entity.GetComponent<ScaleComponent>().Factor = 2;
        }

        return Task.CompletedTask;
    }

    public Task OnExecute(Context context)
    {
        // Get all entities that have both ScaleComponent and SizeComponent
        using var candidates = context.GroupOf(
            typeof(ScaleComponent),
            typeof(SizeComponent));

        // Iterate over all entities
        foreach (var entity in candidates)
        {
            // Get the SizeComponent
            var sizeComponent = entity.GetComponent<SizeComponent>();
            // Get the ScaleComponent's factor
            var factor = entity.GetComponent<ScaleComponent>().Factor;

            // Resize the size component
            sizeComponent.Width = (int)(sizeComponent.Width * factor);
            sizeComponent.Height = (int)(sizeComponent.Height * factor);

            // Remove ScaleComponent from the entity
            entity.RemoveComponent<ScaleComponent>();
        }

        return Task.CompletedTask;
    }
}