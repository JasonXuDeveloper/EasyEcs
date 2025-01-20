using System.Threading.Tasks;

namespace EasyEcs.Core;

public interface IExecuteSystem
{
    int ExecuteFrequency => 1;
    Task OnExecute(Context context);
}