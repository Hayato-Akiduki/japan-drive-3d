; 日本一周ドライブ3D のインストーラー（NSIS）
; 作り方は tools/build-release.ps1 を使う（このファイルを直接 makensis に渡してもよい）
;   makensis /DVERSION=1.2.0 /DSRCDIR=..\配布\日本一周ドライブ3D /DOUTFILE=..\配布\JapanDrive3D-Setup-1.2.0.exe JapanDrive3D.nsi
;
; ・管理者権限なしでユーザーのフォルダ（%LOCALAPPDATA%\Programs\JapanDrive3D）に入れる
;   → ゲームの自動更新が exe を入れ替えられるように
; ・スタートメニューとデスクトップにショートカット、Windows の「アプリ」からアンインストールできる

Unicode true
!include "MUI2.nsh"

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef SRCDIR
  !define SRCDIR "..\配布\日本一周ドライブ3D"
!endif
!ifndef OUTFILE
  !define OUTFILE "..\配布\JapanDrive3D-Setup-${VERSION}.exe"
!endif

!define APPNAME "日本一周ドライブ3D"
!define EXENAME "JapanDrive3D.exe"
!define PUBLISHER "Hayato-Akiduki"
!define URL "https://github.com/Hayato-Akiduki/japan-drive-3d"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\JapanDrive3D"

Name "${APPNAME}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\JapanDrive3D"
InstallDirRegKey HKCU "Software\JapanDrive3D" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1041 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1041 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1041 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1041 "FileDescription" "${APPNAME} インストーラー"
VIAddVersionKey /LANG=1041 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1041 "LegalCopyright" "${PUBLISHER}"

; ---- 画面 ----
!define MUI_ICON "..\JapanDrive3D\icon.ico"
!define MUI_UNICON "..\JapanDrive3D\icon.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_WELCOMEPAGE_TITLE "${APPNAME} のセットアップへようこそ"
!define MUI_WELCOMEPAGE_TEXT "日本の高速道路を走って日本一周する3Dドライブゲームをインストールします。$\r$\n$\r$\n管理者の権限はいりません。インストール後も、ゲームが自動で新しいバージョンに更新できます。$\r$\n$\r$\n「次へ」を押して続けてください。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT "${APPNAME} を起動する"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\遊び方.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "遊び方を読む"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Japanese"

; ---- インストール ----
Section "${APPNAME}（本体）" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  File "${SRCDIR}\${EXENAME}"
  File "${SRCDIR}\遊び方.txt"
  Delete "$INSTDIR\JapanDrive3D.old.exe"   ; 自動更新で残った古い exe
  WriteUninstaller "$INSTDIR\uninstall.exe"

  CreateShortcut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\${EXENAME}" 0

  ; Windows の「アプリ」一覧に出す
  WriteRegStr HKCU "Software\JapanDrive3D" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXENAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "URLInfoAbout" "${URL}"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" 75000
SectionEnd

Section "デスクトップにショートカットを作る" SecDesktop
  SetOutPath "$INSTDIR"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\${EXENAME}" 0
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "ゲーム本体と遊び方メモ（約72MB）。スタートメニューにも登録します。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "デスクトップから起動できるようにします。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---- アンインストール ----
Section "Uninstall"
  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\JapanDrive3D.old.exe"
  Delete "$INSTDIR\遊び方.txt"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\JapanDrive3D"

  ; セーブデータ（スタンプや記録）は、聞いてから消す。何も聞かない /S のときは残す
  MessageBox MB_YESNO|MB_ICONQUESTION "セーブデータ（名所スタンプや記録）も削除しますか？$\r$\n「いいえ」を選ぶと残るので、もう一度インストールしたときに続きから遊べます。" /SD IDNO IDNO keep
    RMDir /r "$LOCALAPPDATA\JapanDrive3D"
  keep:
SectionEnd
