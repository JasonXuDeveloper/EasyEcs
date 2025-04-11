using System.Threading.Tasks;

namespace EasyEcs.Core.Systems;

public interface IExecuteSystem
{
    int ExecuteFrequency => 1;
    ValueTask OnExecute(Context context);
}