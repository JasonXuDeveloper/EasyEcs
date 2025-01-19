using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.UnitTest.Components;

namespace EasyEcs.UnitTest.Systems;

public class ModificationSystem: SystemBase, IExecuteSystem
{
    public override int Frequency => 5;
    public override int Priority => -1;

    public ValueTask Execute(Context context)
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
}