; ==========================================================================
; Your Craft Launcher (YCL) - Inno Setup 安装包脚本
; --------------------------------------------------------------------------
; 版本: 1.0.0
; 说明: 本脚本用于将 publish 目录下的发布文件打包成 Windows 安装包
;
; 【如何编译生成安装包】
;   1. 先安装 Inno Setup 6（免费开源）: https://jrsoftware.org/isdl.php
;      默认安装路径: C:\Program Files (x86)\Inno Setup 6\
;   2. 在项目根目录 (e:\Your Craft Launcher) 下执行以下命令之一:
;      a) 命令行编译（推荐，用于自动化）:
;         "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\YCL.iss
;      b) 双击 installer\YCL.iss 用 Inno Setup Compiler 图形界面打开，
;         然后点击菜单 Build -> Compile (或按 Ctrl+F7)
;   3. 编译成功后，安装包会生成在:
;      installer\output\YCL-Setup-1.0.0.exe
;
; 【编译前前置条件】
;   - 已执行: dotnet publish YCL\YCL.csproj -c Release -o publish
;     (生成 publish 目录及其中的 YCL.exe 和所有依赖 DLL)
;   - 本脚本中 Source 路径 "..\publish\*" 指向项目根目录下的 publish 文件夹
;
; 【关于应用程序图标】
;   当前项目未提供 .ico 图标文件，YCL.exe 使用 .NET 默认图标。
;   卸载显示图标(UninstallDisplayIcon)直接指向 YCL.exe，Windows 会自动
;   从中提取图标。若后续添加了 YCL.ico，可在 YCL.csproj 中启用
;   <ApplicationIcon>YCL.ico</ApplicationIcon> 重新发布即可。
; ==========================================================================

[Setup]
; 应用程序名称（显示在安装向导、控制面板程序列表中）
AppName=Your Craft Launcher
; 应用程序版本号
AppVersion=1.0.0
; 发布者名称
AppPublisher=YCL Project
; 发布者网址
AppPublisherURL=https://github.com/YCL/YCL
; 默认安装目录; {autopf} 会根据 32/64 位自动选择 Program Files 或 Program Files (x86)
DefaultDirName={autopf}\YCL
; 默认开始菜单组名
DefaultGroupName=Your Craft Launcher
; 不显示"选择开始菜单文件夹"页面
DisableProgramGroupPage=yes
; 安装包输出目录（相对于本 .iss 文件所在目录）
OutputDir=output
; 安装包输出文件名（最终生成 YCL-Setup-1.0.0.exe）
OutputBaseFilename=YCL-Setup-1.0.0
; 压缩算法（LZMA2 压缩率高）
Compression=lzma2
; 启用固实压缩（所有文件作为一个整体压缩，体积更小）
SolidCompression=yes
; 使用现代风格向导界面
WizardStyle=modern
; 仅允许在 x64 兼容系统上安装
ArchitecturesAllowed=x64compatible
; 在 64 位模式下安装
ArchitecturesInstallIn64BitMode=x64compatible
; 卸载时显示的图标（从 YCL.exe 中提取）
UninstallDisplayIcon={app}\YCL.exe
; 卸载时显示的程序名称
UninstallDisplayName=Your Craft Launcher

[Languages]
; 默认语言: 简体中文（需 Inno Setup 6 自带 ChineseSimplified.isl）
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
; 备选语言: 英文
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; 附加任务: 创建桌面快捷方式（默认勾选，安装过则记住选择）
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: checkedonce

[Files]
; 将 publish 目录下所有文件（含子目录）复制到安装目录
; ignoreversion: 不比较版本号强制覆盖
; recursesubdirs: 递归子目录
; createallsubdirs: 创建所有子目录（包括空目录）
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单 - 主程序快捷方式
Name: "{group}\Your Craft Launcher"; Filename: "{app}\YCL.exe"
; 开始菜单 - 卸载快捷方式
Name: "{group}\卸载 Your Craft Launcher"; Filename: "{uninstallexe}"
; 桌面快捷方式（依赖 desktopicon 任务）
Name: "{autodesktop}\Your Craft Launcher"; Filename: "{app}\YCL.exe"; Tasks: desktopicon

[Run]
; 安装完成后可选立即启动程序（仅非静默安装时显示选项）
Filename: "{app}\YCL.exe"; Description: "立即启动 Your Craft Launcher"; Flags: nowait postinstall skipifsilent
