using System.Threading.Tasks;

namespace EasyEcs.Core;

public interface IEndSystem
{
    ValueTask OnEnd(Context context);
}