using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.UnitTest.Components;

namespace EasyEcs.UnitTest.Systems;

public class ModificationSystem: SystemBase, IExecuteSystem, IEndSystem
{
    public override int ExecuteFrequency => 5;
    public override int Priority => -1;

    /// <summary>
    /// Halve the size of all entities that have a SizeComponent
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public ValueTask OnExecute(Context context)
    {
        var candidates = context.GroupOf(
            typeof(SizeComponent));

        foreach (var entity in candidates)
        {
            var comp = entity.AddComponent<ScaleComponent>();
            comp.Factor = 0.5f;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Make size to 0
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public ValueTask OnEnd(Context context)
    {
        var candidates = context.GroupOf(
            typeof(SizeComponent));

        foreach (var entity in candidates)
        {
            var sizeComponent = entity.GetComponent<SizeComponent>();
            sizeComponent.Width = 0;
            sizeComponent.Height = 0;
        }
        
        return ValueTask.CompletedTask;
    }
}