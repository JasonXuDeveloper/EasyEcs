using System;
using Cysharp.Threading.Tasks;

namespace EasyEcs.Core.Systems;

public interface IEndSystem
{
    UniTask OnEnd(Context context);

    internal async UniTask Execute(Context context, Action<Exception> onError)
    {
        try
        {
            await OnEnd(context);
        }
        catch (Exception e)
        {
            onError?.Invoke(e);
        }
    }
}