using System.Threading.Tasks;

namespace EasyEcs.Core.Systems;

public interface IEndSystem
{
    ValueTask OnEnd(Context context);
}