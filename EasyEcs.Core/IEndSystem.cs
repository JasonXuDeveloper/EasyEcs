using System.Threading.Tasks;

namespace EasyEcs.Core;

public interface IEndSystem
{
    Task OnEnd(Context context);
}