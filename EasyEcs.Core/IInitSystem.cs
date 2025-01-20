using System.Threading.Tasks;

namespace EasyEcs.Core;

public interface IInitSystem
{
    Task OnInit(Context context);
}