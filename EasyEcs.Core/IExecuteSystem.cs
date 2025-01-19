using System.Threading.Tasks;

namespace EasyEcs.Core;

public interface IExecuteSystem
{
    ValueTask OnExecute(Context context);
}