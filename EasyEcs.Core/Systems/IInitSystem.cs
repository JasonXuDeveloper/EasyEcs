using System.Threading.Tasks;

namespace EasyEcs.Core.Systems;

public interface IInitSystem
{
    ValueTask OnInit(Context context);
}