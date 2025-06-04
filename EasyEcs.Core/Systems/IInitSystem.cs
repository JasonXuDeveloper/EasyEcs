using Cysharp.Threading.Tasks;

namespace EasyEcs.Core.Systems;

public interface IInitSystem
{
    UniTask OnInit(Context context);
}