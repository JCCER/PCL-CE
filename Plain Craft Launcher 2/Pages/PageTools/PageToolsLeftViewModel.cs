using PCL.Core.App;
using PCL.Core.ViewModel;

namespace PCL;

/// <summary>工具页面左侧导航栏的视图模型。</summary>
public class PageToolsLeftViewModel : ViewModelBase
{
    public bool IsHiddenPage(bool isGameLinkChecked, bool isTestChecked, bool isForceShow)
    {
        var hide = Config.Preference.Hide;
        var isHiddenPage = isGameLinkChecked && hide.ToolsGameLink || isTestChecked && hide.ToolsTest;
        return isForceShow ? false : isHiddenPage;
    }

    public FormMain.PageSubType ResolveInitialPageId()
    {
        // 联机功能已移除，工具页默认返回「测试」子页面
        return FormMain.PageSubType.ToolsTest;
    }
}
