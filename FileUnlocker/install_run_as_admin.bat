cd /d %~dp0

REM === Original commands (backup) ===
REM @Reg Add "HKEY_CLASSES_ROOT\*\shell\FileUnlocker" /VE /D "FileUnlocker" /F >Nul
REM @Reg Add "HKEY_CLASSES_ROOT\*\shell\FileUnlocker" /V "Icon" /D "\"%CD%\key.ico\" /F >Nul
REM @Reg Add "HKEY_CLASSES_ROOT\*\shell\FileUnlocker\command" /VE /D "\"%CD%\FileUnlocker.exe\" \"%%1\"" /F >Nul
REM
REM @Reg Add "HKEY_CLASSES_ROOT\Directory\shell\FileUnlocker" /VE /D "FileUnlocker" /F >Nul
REM @Reg Add "HKEY_CLASSES_ROOT\Directory\shell\FileUnlocker" /V "Icon" /D "\"%CD%\key.ico\" /F >Nul
REM @Reg Add "HKEY_CLASSES_ROOT\Directory\shell\FileUnlocker\command" /VE /D "\"%CD%\FileUnlocker.exe\" \"%%1\"" /F >Nul
REM === End original commands ===

REM === File context menu (supports multi-select) ===
@Reg Add "HKEY_CLASSES_ROOT\*\shell\FileUnlocker" /VE /D "FileUnlocker" /F >Nul
@Reg Add "HKEY_CLASSES_ROOT\*\shell\FileUnlocker" /V "Icon" /D "\"%CD%\key.ico\"" /F >Nul
@Reg Add "HKEY_CLASSES_ROOT\*\shell\FileUnlocker" /V "MultiSelectModel" /D "Player" /F >Nul
@Reg Add "HKEY_CLASSES_ROOT\*\shell\FileUnlocker\command" /VE /D "\"%CD%\FileUnlocker.exe\" -norestartmanagerdetect \"%%1\" \"%%2\" \"%%3\" \"%%4\" \"%%5\" \"%%6\" \"%%7\" \"%%8\" \"%%9\"" /F >Nul

REM === Directory context menu (supports multi-select) ===
@Reg Add "HKEY_CLASSES_ROOT\Directory\shell\FileUnlocker" /VE /D "FileUnlocker" /F >Nul
@Reg Add "HKEY_CLASSES_ROOT\Directory\shell\FileUnlocker" /V "Icon" /D "\"%CD%\key.ico\"" /F >Nul
@Reg Add "HKEY_CLASSES_ROOT\Directory\shell\FileUnlocker" /V "MultiSelectModel" /D "Player" /F >Nul
@Reg Add "HKEY_CLASSES_ROOT\Directory\shell\FileUnlocker\command" /VE /D "\"%CD%\FileUnlocker.exe\" -norestartmanagerdetect \"%%1\" \"%%2\" \"%%3\" \"%%4\" \"%%5\" \"%%6\" \"%%7\" \"%%8\" \"%%9\"" /F >Nul

REM === Directory background context menu (right-click on empty space in a folder) ===
@Reg Add "HKEY_CLASSES_ROOT\Directory\Background\shell\FileUnlocker" /VE /D "FileUnlocker" /F >Nul
@Reg Add "HKEY_CLASSES_ROOT\Directory\Background\shell\FileUnlocker" /V "Icon" /D "\"%CD%\key.ico\"" /F >Nul
@Reg Add "HKEY_CLASSES_ROOT\Directory\Background\shell\FileUnlocker\command" /VE /D "\"%CD%\FileUnlocker.exe\" -norestartmanagerdetect \"%V\"" /F >Nul
