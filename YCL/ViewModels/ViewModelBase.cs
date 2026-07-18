using CommunityToolkit.Mvvm.ComponentModel;

namespace YCL.ViewModels
{
    /// <summary>
    /// 所有 ViewModel 的基类。
    /// 继承自 CommunityToolkit.Mvvm 的 ObservableObject，
    /// 让子类具备"属性变更通知"的能力（界面会自动响应属性变化而更新）。
    /// </summary>
    public abstract class ViewModelBase : ObservableObject
    {
    }
}
