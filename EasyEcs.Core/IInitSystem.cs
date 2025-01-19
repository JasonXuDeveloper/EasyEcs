using System.Threading.Tasks;

namespace EasyEcs.Core;

public interface IInitSystem
{
    ValueTask OnInit(Context context);
}