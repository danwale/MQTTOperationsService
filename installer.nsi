/*
MQTT Operations Service
Installer Script
Written by Daniel Wale
*/

;--------------------------------
;Include Modern UI

  !include "MUI2.nsh"

;--------------------------------

!define NAME "MQTT Operations Service"
!define SERVICE_NAME "MQTTOperationsService"
!define REGPATH_UNINSTSUBKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${SERVICE_NAME}"
!define DISPLAY_NAME "MQTT Operations Service"
Name "${NAME}"
OutFile "${SERVICE_NAME}-Installer.exe"
Unicode True
RequestExecutionLevel Admin ; Request admin rights
InstallDir "$PROGRAMFILES64\MQTTOperationsService"
InstallDirRegKey HKLM "${REGPATH_UNINSTSUBKEY}" "UninstallString"

;--------------------------------
;Interface Settings

  !define MUI_ABORTWARNING

;--------------------------------
;Pages

  !insertmacro MUI_PAGE_LICENSE ".\bin\Release\net5.0\publish\LICENSE.txt"
  !insertmacro MUI_PAGE_DIRECTORY
  !insertmacro MUI_PAGE_INSTFILES
  
  !insertmacro MUI_UNPAGE_CONFIRM
  !insertmacro MUI_UNPAGE_INSTFILES
  
;--------------------------------

;--------------------------------
;Languages
 
  !insertmacro MUI_LANGUAGE "English"

!macro EnsureAdminRights
  UserInfo::GetAccountType
  Pop $0
  ${If} $0 != "admin" ; Require admin rights on WinNT4+
    MessageBox MB_IconStop "Administrator rights required!"
    SetErrorLevel 740 ; ERROR_ELEVATION_REQUIRED
    Quit
  ${EndIf}
!macroend

Function .onInit
  SetShellVarContext All
  !insertmacro EnsureAdminRights
FunctionEnd

Function un.onInit
  SetShellVarContext All
  !insertmacro EnsureAdminRights
FunctionEnd

Section "Program files (Required)"
  SectionIn Ro

  SetOutPath $InstDir
  WriteUninstaller "$InstDir\Uninst.exe"
  WriteRegStr HKLM "${REGPATH_UNINSTSUBKEY}" "DisplayName" "${NAME}"
  WriteRegStr HKLM "${REGPATH_UNINSTSUBKEY}" "DisplayIcon" "$InstDir\MQTTOperationsService.exe"
  WriteRegStr HKLM "${REGPATH_UNINSTSUBKEY}" "Publisher" "Daniel Wale"
  WriteRegDWORD HKLM "${REGPATH_UNINSTSUBKEY}" "EstimatedSize" 76925
  WriteRegStr HKLM "${REGPATH_UNINSTSUBKEY}" "DisplayVersion" "1.0.0"
  WriteRegStr HKLM "${REGPATH_UNINSTSUBKEY}" "UninstallString" '"$InstDir\Uninst.exe"'
  WriteRegStr HKLM "${REGPATH_UNINSTSUBKEY}" "QuietUninstallString" '"$InstDir\Uninst.exe" /S'
  WriteRegDWORD HKLM "${REGPATH_UNINSTSUBKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${REGPATH_UNINSTSUBKEY}" "NoRepair" 1
  
  File .\bin\Release\net5.0\publish\*.dll
  File .\bin\Release\net5.0\publish\*.json
  File .\bin\Release\net5.0\publish\*.exe
  File .\bin\Release\net5.0\publish\*.pdb
  File .\bin\Release\net5.0\publish\*.ps1
  File .\bin\Release\net5.0\publish\*.txt
  
  SetOutPath $InstDir\cs
  File .\bin\Release\net5.0\publish\cs\*.dll
  SetOutPath $InstDir\de
  File .\bin\Release\net5.0\publish\de\*.dll
  SetOutPath $InstDir\es
  File .\bin\Release\net5.0\publish\es\*.dll
  SetOutPath $InstDir\fr
  File .\bin\Release\net5.0\publish\fr\*.dll
  SetOutPath $InstDir\it
  File .\bin\Release\net5.0\publish\it\*.dll
  SetOutPath $InstDir\ja
  File .\bin\Release\net5.0\publish\ja\*.dll
  SetOutPath $InstDir\ko
  File .\bin\Release\net5.0\publish\ko\*.dll
  SetOutPath $InstDir\pl
  File .\bin\Release\net5.0\publish\pl\*.dll
  SetOutPath $InstDir\pt-BR
  File .\bin\Release\net5.0\publish\pt-BR\*.dll
  SetOutPath $InstDir\ref
  File .\bin\Release\net5.0\publish\ref\*.dll
  SetOutPath $InstDir\ru
  File .\bin\Release\net5.0\publish\ru\*.dll
  SetOutPath $InstDir\tr
  File .\bin\Release\net5.0\publish\tr\*.dll
  SetOutPath $InstDir\zh-Hans
  File .\bin\Release\net5.0\publish\zh-Hans\*.dll
  SetOutPath $InstDir\zh-Hant
  File .\bin\Release\net5.0\publish\zh-Hant\*.dll
  
  SetOutPath $InstDir\runtimes\win\lib\net5.0\Modules\CimCmdlets
  File .\bin\Release\net5.0\publish\runtimes\win\lib\net5.0\Modules\CimCmdlets\*
  SetOutPath $InstDir\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Diagnostics
  File .\bin\Release\net5.0\publish\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Diagnostics\*
  SetOutPath $InstDir\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Host
  File .\bin\Release\net5.0\publish\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Host\*
  SetOutPath $InstDir\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Management
  File .\bin\Release\net5.0\publish\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Management\*
  SetOutPath $InstDir\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Security
  File .\bin\Release\net5.0\publish\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Security\*
  SetOutPath $InstDir\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Utility
  File .\bin\Release\net5.0\publish\runtimes\win\lib\net5.0\Modules\Microsoft.PowerShell.Utility\*
  SetOutPath $InstDir\runtimes\win\lib\net5.0\Modules\Microsoft.WSMan.Management
  File .\bin\Release\net5.0\publish\runtimes\win\lib\net5.0\Modules\Microsoft.WSMan.Management\*
  SetOutPath $InstDir\runtimes\win\lib\net5.0\Modules\PSDiagnostics
  File .\bin\Release\net5.0\publish\runtimes\win\lib\net5.0\Modules\PSDiagnostics\*
  
  SetOutPath $InstDir\runtimes\unix\lib\net5.0\Modules\Microsoft.PowerShell.Host
  File .\bin\Release\net5.0\publish\runtimes\unix\lib\net5.0\Modules\Microsoft.PowerShell.Host\*
  SetOutPath $InstDir\runtimes\unix\lib\net5.0\Modules\Microsoft.PowerShell.Management
  File .\bin\Release\net5.0\publish\runtimes\unix\lib\net5.0\Modules\Microsoft.PowerShell.Management\*
  SetOutPath $InstDir\runtimes\unix\lib\net5.0\Modules\Microsoft.PowerShell.Security
  File .\bin\Release\net5.0\publish\runtimes\unix\lib\net5.0\Modules\Microsoft.PowerShell.Security\*
  SetOutPath $InstDir\runtimes\unix\lib\net5.0\Modules\Microsoft.PowerShell.Utility
  File .\bin\Release\net5.0\publish\runtimes\unix\lib\net5.0\Modules\Microsoft.PowerShell.Utility\*
  
  ExecWait 'sc.exe create ${SERVICE_NAME} error= "severe" displayname= "${DISPLAY_NAME}" type= "own" start= "auto" binpath= "$INSTDIR\MQTTOperationsService.exe"'
  ExecWait 'sc.exe description ${SERVICE_NAME} "Trigger PowerShell scripts via MQTT and send the output via MQTT"'
SectionEnd

;--------------------------------
;Uninstaller Section

Section "Uninstall"
  ExecWait 'sc.exe delete ${SERVICE_NAME}'
  Delete "$InstDir\Uninst.exe"
  Delete "$InstDir\*"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "${REGPATH_UNINSTSUBKEY}"
SectionEnd
