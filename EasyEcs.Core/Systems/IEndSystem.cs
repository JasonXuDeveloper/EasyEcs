using System.Threading.Tasks;

namespace EasyEcs.Core.Systems;

public interface IEndSystem
{
    Task OnEnd(Context context);
}