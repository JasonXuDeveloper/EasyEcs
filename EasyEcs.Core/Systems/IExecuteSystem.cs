using Cysharp.Threading.Tasks;

namespace EasyEcs.Core.Systems;

public interface IExecuteSystem
{
    int ExecuteFrequency => 1;
    UniTask OnExecute(Context context);
}