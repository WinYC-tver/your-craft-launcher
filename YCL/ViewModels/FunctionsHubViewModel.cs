using CommunityToolkit.Mvvm.Input;
using YCL.Services;

namespace YCL.ViewModels
{
    /// <summary>
    /// 功能板块 ViewModel：作为卡片网格导航中枢，
    /// 通过 <see cref="INavigationService"/> 跳转到对应子页面。
    /// </summary>
    public partial class FunctionsHubViewModel : ViewModelBase
    {
        private readonly INavigationService _navigationService;

        public FunctionsHubViewModel(INavigationService navigationService)
        {
            _navigationService = navigationService;
        }

        /// <summary>导航命令：根据页面键跳转到对应子页面（如 Multiplayer/Instance/Java 等）</summary>
        [RelayCommand]
        private void Navigate(string pageKey) => _navigationService.NavigateTo(pageKey);
    }
}
