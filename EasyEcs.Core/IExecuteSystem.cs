using System.Threading.Tasks;

namespace EasyEcs.Core;

public interface IExecuteSystem
{
    ValueTask Execute(Context context);
}