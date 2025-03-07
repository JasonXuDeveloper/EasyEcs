using System.Threading.Tasks;

namespace EasyEcs.Core.Systems;

public interface IInitSystem
{
    Task OnInit(Context context);
}