@echo off
chcp 65001 >nul
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
pushd "%ROOT%" >nul || (
    echo Failed to enter publish directory: %ROOT%
    exit /b 1
)

set "REPO=%ROOT%.."
set "MENU_SCRIPT=%ROOT%scripts\publish-menu.ps1"
set "DEPLOY_CONTEXT_SCRIPT=%ROOT%scripts\deploy-context.ps1"
set "REMOTE_BUILD_SCRIPT=%ROOT%deploy-remote-build.sh"
set "REMOTE_COMPOSE_SCRIPT=%ROOT%deploy-remote-compose.sh"
set "CONFIG_SCRIPT=%ROOT%deploy-config-remote.sh"
set "TMP_ROOT=%ROOT%tmp\deploy"
set "STATE_ROOT=%ROOT%state"

if not defined LEADFLOW_SERVER if defined ORBITA_SERVER set "LEADFLOW_SERVER=%ORBITA_SERVER%"
if not defined LEADFLOW_SERVER set "LEADFLOW_SERVER=root@163.5.153.207"
if not defined LEADFLOW_SSH_PORT set "LEADFLOW_SSH_PORT=22"
if not defined LEADFLOW_REMOTE_DIR set "LEADFLOW_REMOTE_DIR=/opt/orbita"
if not defined LEADFLOW_SKIP_SECRETS_SYNC set "LEADFLOW_SKIP_SECRETS_SYNC=0"
if not defined LEADFLOW_SKIP_DOCKER_PRUNE set "LEADFLOW_SKIP_DOCKER_PRUNE=0"
if not defined LEADFLOW_DOCKER_PRUNE_DEEP set "LEADFLOW_DOCKER_PRUNE_DEEP=0"

set "SERVER=%LEADFLOW_SERVER%"
set "REMOTE_DIR=%LEADFLOW_REMOTE_DIR%"
set "SKIP_SECRETS_SYNC=%LEADFLOW_SKIP_SECRETS_SYNC%"

if not exist "%MENU_SCRIPT%" (
    echo Publish menu script not found: %MENU_SCRIPT%
    exit /b 1
)

if not exist "%DEPLOY_CONTEXT_SCRIPT%" (
    echo Deploy context helper not found: %DEPLOY_CONTEXT_SCRIPT%
    exit /b 1
)

if not exist "%REMOTE_BUILD_SCRIPT%" (
    echo Remote build helper not found: %REMOTE_BUILD_SCRIPT%
    exit /b 1
)

if not exist "%REMOTE_COMPOSE_SCRIPT%" (
    echo Remote compose helper not found: %REMOTE_COMPOSE_SCRIPT%
    exit /b 1
)

call :ensure_publish_paths
if errorlevel 1 exit /b 1

if not exist "%CONFIG_SCRIPT%" (
    echo Remote config helper not found: %CONFIG_SCRIPT%
    exit /b 1
)

call :require_command ssh
if errorlevel 1 exit /b 1
call :require_command scp
if errorlevel 1 exit /b 1
call :require_command tar
if errorlevel 1 exit /b 1

call :resolve_ssh_key
call :build_transport_args
call :resolve_secret_file

if not "%~1"=="" goto cli

:menu
set "MENU_RESULT=%TEMP%\leadflow-publish-menu-%RANDOM%-%RANDOM%.txt"
if exist "!MENU_RESULT!" del /f /q "!MENU_RESULT!" >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -File "%MENU_SCRIPT%" -ResultPath "!MENU_RESULT!"
if errorlevel 1 (
    echo Failed to open publish menu.
    if exist "!MENU_RESULT!" del /f /q "!MENU_RESULT!" >nul 2>&1
    exit /b 1
)

set "choice="
if exist "!MENU_RESULT!" (
    set /p "choice="<"!MENU_RESULT!"
    del /f /q "!MENU_RESULT!" >nul 2>&1
)

if not defined choice goto menu
if /i "!choice!"=="0" goto done

call :run_selection "!choice!"
set "SELECTION_ERROR=!ERRORLEVEL!"
if !SELECTION_ERROR! neq 0 (
    echo.
    echo Publish failed with error code !SELECTION_ERROR!.
)
call :pause_prompt
goto menu

:cli
call :parse_cli %*
if errorlevel 1 exit /b 1
call :run_selection "!CLI_TARGETS!"
goto done

:parse_cli
set "CLI_TARGETS="
if "%~1"=="" (
    set "CLI_TARGETS=all"
    exit /b 0
)

if /i "%~1"=="-Target" (
    if "%~2"=="" (
        echo Missing value after -Target.
        exit /b 1
    )
    set "CLI_TARGETS=%~2"
    exit /b 0
)

if /i "%~1"=="--target" (
    if "%~2"=="" (
        echo Missing value after --target.
        exit /b 1
    )
    set "CLI_TARGETS=%~2"
    exit /b 0
)

:cli_collect
if "%~1"=="" goto cli_collect_done
if defined CLI_TARGETS (
    set "CLI_TARGETS=!CLI_TARGETS!,%~1"
) else (
    set "CLI_TARGETS=%~1"
)
shift
goto cli_collect
:cli_collect_done
exit /b 0

:run_selection
set "selection=%~1"

call :ensure_publish_paths
if errorlevel 1 exit /b 1
call :build_transport_args

if not "%SKIP_SECRETS_SYNC%"=="1" (
    call :sync_secrets
    if errorlevel 1 exit /b 1
)

if /i "!selection!"=="all" (
    echo.
    echo [publish] config
    call :publish_target config
    if errorlevel 1 exit /b 1
    echo.
    echo [publish] orbita-api
    call :publish_target orbita-api
    if errorlevel 1 exit /b 1
    echo.
    echo [publish] orbita-web
    call :publish_target orbita-web
    if errorlevel 1 exit /b 1
    echo.
    echo [publish] notifybot
    call :publish_target notifybot
    if errorlevel 1 exit /b 1
    exit /b 0
)

set "selection=!selection:,= !"
set "selection=!selection:;= !"
echo.
echo [publish] targets:!selection!
for %%T in (!selection!) do (
    if "%%T"=="" (
        rem Skip empty tokens from duplicate separators.
    ) else (
        echo.
        echo [publish] %%T
        call :publish_target %%T
        if errorlevel 1 exit /b 1
    )
)

exit /b 0

:publish_target
set "TARGET=%~1"
call :ensure_publish_paths
if errorlevel 1 exit /b 1

if /i "!TARGET!"=="config" (
    call :publish_config
    if errorlevel 1 exit /b 1
    exit /b 0
)

call :configure_target "!TARGET!"
if errorlevel 1 exit /b 1

if /i not "!TARGET!"=="notifybot" (
    call :sync_compose_always
    if errorlevel 1 exit /b 1
)

set "TARGET_ROOT=!TMP_ROOT!\!TARGET!"
set "STAGE_DIR=!TARGET_ROOT!\stage"
set "ARCHIVE=!TMP_ROOT!\leadflow-!TARGET!-context.tar.gz"

if exist "!TARGET_ROOT!" rmdir /s /q "!TARGET_ROOT!"
if exist "!ARCHIVE!" del /f /q "!ARCHIVE!"
mkdir "!STAGE_DIR!" || exit /b 1

echo == Staging !TARGET! build context ==
for %%P in (!CONTEXT_ITEMS!) do (
    call :copy_context_path "%%P"
    if errorlevel 1 exit /b 1
)

set "PLAN_FILE=!TARGET_ROOT!\deploy-plan.json"
set "DEPLOY_MODE=full"

echo == Checking !TARGET! changes ==
powershell -NoProfile -ExecutionPolicy Bypass -File "!DEPLOY_CONTEXT_SCRIPT!" -Action plan -Target "!TARGET!" -StageDir "!STAGE_DIR!" -StateDir "!STATE_ROOT!" -PlanPath "!PLAN_FILE!"
if errorlevel 1 exit /b 1

if exist "!PLAN_FILE!.mode" (
    set /p "DEPLOY_MODE="<"!PLAN_FILE!.mode"
)

if /i "!DEPLOY_MODE!"=="SKIP" (
    echo == !TARGET! unchanged; image rebuild skipped ==
    if /i not "!TARGET!"=="notifybot" (
        echo == Recreating !COMPOSE_SERVICE! with current compose ==
        scp !SCP_ARGS! "!REMOTE_COMPOSE_SCRIPT!" "!SERVER!:/tmp/deploy-remote-compose.sh"
        if errorlevel 1 exit /b 1
        ssh !SSH_ARGS! !SERVER! "sed -i 's/\r$//' /tmp/deploy-remote-compose.sh && chmod +x /tmp/deploy-remote-compose.sh && bash /tmp/deploy-remote-compose.sh !COMPOSE_SERVICE! !REMOTE_DIR!"
        if errorlevel 1 exit /b 1
    )
    if exist "!TARGET_ROOT!" rmdir /s /q "!TARGET_ROOT!"
    exit /b 0
)

if /i "!DEPLOY_MODE!"=="delta" (
    call :ensure_remote_build_cache
    if errorlevel 1 (
        powershell -NoProfile -ExecutionPolicy Bypass -File "!DEPLOY_CONTEXT_SCRIPT!" -Action upgrade-full -PlanPath "!PLAN_FILE!"
        if errorlevel 1 exit /b 1
        set "DEPLOY_MODE=full"
    )
)

:publish_target_deploy
echo == Packing !TARGET! build context ==
powershell -NoProfile -ExecutionPolicy Bypass -File "!DEPLOY_CONTEXT_SCRIPT!" -Action pack -Target "!TARGET!" -StageDir "!STAGE_DIR!" -StateDir "!STATE_ROOT!" -ArchivePath "!ARCHIVE!" -PlanPath "!PLAN_FILE!"
if errorlevel 1 exit /b 1

echo == Uploading !TARGET! ==
scp !SCP_ARGS! "!ARCHIVE!" "!SERVER!:/tmp/leadflow-!TARGET!-context.tar.gz"
if errorlevel 1 exit /b 1

scp !SCP_ARGS! "!REMOTE_BUILD_SCRIPT!" "!SERVER!:/tmp/deploy-remote-build.sh"
if errorlevel 1 exit /b 1

echo == Building and deploying !TARGET! on !SERVER! ==
ssh !SSH_ARGS! !SERVER! "sed -i 's/\r$//' /tmp/deploy-remote-build.sh && chmod +x /tmp/deploy-remote-build.sh && LEADFLOW_SKIP_DOCKER_PRUNE=!LEADFLOW_SKIP_DOCKER_PRUNE! LEADFLOW_DOCKER_PRUNE_DEEP=!LEADFLOW_DOCKER_PRUNE_DEEP! bash /tmp/deploy-remote-build.sh /tmp/leadflow-!TARGET!-context.tar.gz !DOCKERFILE! !IMAGE_TAG! !COMPOSE_SERVICE! !REMOTE_DIR! !DEPLOY_MODE!"
set "BUILD_RC=!ERRORLEVEL!"
if !BUILD_RC! equ 42 (
    if /i not "!DEPLOY_MODE!"=="full" (
        echo.
        echo == Remote build cache incomplete; retrying with full upload ==
        powershell -NoProfile -ExecutionPolicy Bypass -File "!DEPLOY_CONTEXT_SCRIPT!" -Action upgrade-full -PlanPath "!PLAN_FILE!"
        if errorlevel 1 exit /b 1
        set "DEPLOY_MODE=full"
        goto publish_target_deploy
    )
)
if !BUILD_RC! neq 0 exit /b !BUILD_RC!

powershell -NoProfile -ExecutionPolicy Bypass -File "!DEPLOY_CONTEXT_SCRIPT!" -Action save -Target "!TARGET!" -StageDir "!STAGE_DIR!" -StateDir "!STATE_ROOT!"
if errorlevel 1 exit /b 1

if exist "!TARGET_ROOT!" rmdir /s /q "!TARGET_ROOT!"
if exist "!ARCHIVE!" del /f /q "!ARCHIVE!"

echo == Finished !TARGET! ==
exit /b 0

:publish_config
set "STAGE_DIR=!TMP_ROOT!\config"
set "REMOTE_STAGING=/tmp/leadflow-orbita-config"
set "PLAN_FILE=!STAGE_DIR!\deploy-plan.json"
set "DEPLOY_MODE=full"

if exist "!STAGE_DIR!" rmdir /s /q "!STAGE_DIR!"
mkdir "!STAGE_DIR!" || exit /b 1

for %%F in (docker-compose.images.yml Caddyfile backup-db.sh) do (
    if not exist "!REPO!\deploy\control-panel\%%F" (
        echo Config file not found: deploy\control-panel\%%F
        exit /b 1
    )
    copy /y "!REPO!\deploy\control-panel\%%F" "!STAGE_DIR!\%%F" >nul
)

echo == Checking config changes ==
powershell -NoProfile -ExecutionPolicy Bypass -File "!DEPLOY_CONTEXT_SCRIPT!" -Action plan -Target "config" -StageDir "!STAGE_DIR!" -StateDir "!STATE_ROOT!" -PlanPath "!PLAN_FILE!"
if errorlevel 1 exit /b 1

if exist "!PLAN_FILE!.mode" (
    set /p "DEPLOY_MODE="<"!PLAN_FILE!.mode"
)

if /i "!DEPLOY_MODE!"=="SKIP" (
    echo == Config unchanged; full upload skipped ==
    call :sync_compose_always
    if errorlevel 1 exit /b 1
    if exist "!STAGE_DIR!" rmdir /s /q "!STAGE_DIR!"
    exit /b 0
)

echo == Uploading server config ==
ssh !SSH_ARGS! !SERVER! "rm -rf !REMOTE_STAGING! && mkdir -p !REMOTE_STAGING!"
if errorlevel 1 exit /b 1

for %%F in (docker-compose.images.yml Caddyfile backup-db.sh) do (
    scp !SCP_ARGS! "!STAGE_DIR!\%%F" "!SERVER!:!REMOTE_STAGING!/%%F"
    if errorlevel 1 exit /b 1
)

scp !SCP_ARGS! "!CONFIG_SCRIPT!" "!SERVER!:/tmp/deploy-config-remote.sh"
if errorlevel 1 exit /b 1

ssh !SSH_ARGS! !SERVER! "sed -i 's/\r$//' /tmp/deploy-config-remote.sh && chmod +x /tmp/deploy-config-remote.sh && bash /tmp/deploy-config-remote.sh !REMOTE_DIR! !REMOTE_STAGING!"
if errorlevel 1 exit /b 1

powershell -NoProfile -ExecutionPolicy Bypass -File "!DEPLOY_CONTEXT_SCRIPT!" -Action save -Target "config" -StageDir "!STAGE_DIR!" -StateDir "!STATE_ROOT!"
if errorlevel 1 exit /b 1

if exist "!STAGE_DIR!" rmdir /s /q "!STAGE_DIR!"

echo == Server config deployed ==
exit /b 0

:ensure_remote_build_cache
set "CACHE_DOCKERFILE=!REMOTE_DIR!/.build-cache/!COMPOSE_SERVICE!/!DOCKERFILE!"
ssh !SSH_ARGS! !SERVER! "test -f \"!CACHE_DOCKERFILE!\""
if errorlevel 1 exit /b 1
exit /b 0

:copy_context_path
set "REL=%~1"
set "SRC=!REPO!\!REL!"
set "DST=!STAGE_DIR!\!REL!"

if not exist "!SRC!" (
    echo Context path not found: !SRC!
    exit /b 1
)

for %%D in ("!DST!") do mkdir "%%~dpD" >nul 2>&1
if exist "!SRC!\*" (
    robocopy "!SRC!" "!DST!" /E /XD bin obj .vs .git node_modules .idea /XF *.user *.suo /NFL /NDL /NJH /NJS /NC /NS >nul
    if errorlevel 8 exit /b 1
) else (
    copy /y "!SRC!" "!DST!" >nul
)

exit /b 0

:configure_target
set "DOCKERFILE="
set "IMAGE_TAG="
set "COMPOSE_SERVICE="
set "CONTEXT_ITEMS="

if /i "%~1"=="orbita-api" (
    set "DOCKERFILE=deploy/control-panel/Dockerfile.api"
    set "IMAGE_TAG=orbita-api:prod"
    set "COMPOSE_SERVICE=api"
    set "CONTEXT_ITEMS=Orbita.Contracts Orbita.Logging Orbita.Api deploy\control-panel\Dockerfile.api deploy\control-panel\docker-compose.images.yml"
)

if /i "%~1"=="orbita-web" (
    set "DOCKERFILE=deploy/control-panel/Dockerfile.web"
    set "IMAGE_TAG=orbita-web:prod"
    set "COMPOSE_SERVICE=web"
    set "CONTEXT_ITEMS=Orbita.Contracts Orbita.Logging Orbita.Web deploy\control-panel\Dockerfile.web deploy\control-panel\docker-compose.images.yml"
)

if /i "%~1"=="notifybot" (
    set "DOCKERFILE=deploy/control-panel/Dockerfile.notifybot"
    set "IMAGE_TAG=notifybot-api:prod"
    set "COMPOSE_SERVICE=notifybot-api"
    set "CONTEXT_ITEMS=src\NotifyBot.Domain src\NotifyBot.Application src\NotifyBot.Infrastructure src\NotifyBot.Api deploy\control-panel\Dockerfile.notifybot"
)

if not defined DOCKERFILE (
    echo Unknown publish target: %~1
    exit /b 1
)

exit /b 0

:ensure_publish_paths
set "ORBITA_COMPOSE_IMAGES=!REPO!\deploy\control-panel\docker-compose.images.yml"
if not exist "!ORBITA_COMPOSE_IMAGES!" (
    echo Compose file not found: !ORBITA_COMPOSE_IMAGES!
    exit /b 1
)
exit /b 0

:sync_compose_always
call :ensure_publish_paths
if errorlevel 1 exit /b 1
echo == Syncing docker-compose.images.yml to !REMOTE_DIR! ==
scp !SCP_ARGS! "!ORBITA_COMPOSE_IMAGES!" "!SERVER!:!REMOTE_DIR!/docker-compose.images.yml"
if errorlevel 1 exit /b 1
exit /b 0

:sync_secrets
if "%SKIP_SECRETS_SYNC%"=="1" exit /b 0
if not exist "%SECRET_FILE%" (
    echo Secrets file not found: %SECRET_FILE%
    exit /b 1
)

findstr /B /C:"TELEGRAM_BOT_TOKEN=" "%SECRET_FILE%" | findstr /R /C:"TELEGRAM_BOT_TOKEN=." >nul
if errorlevel 1 (
    echo TELEGRAM_BOT_TOKEN is empty in %SECRET_FILE%
    exit /b 1
)

echo == Syncing .env to !REMOTE_DIR! ==
scp !SCP_ARGS! "!SECRET_FILE!" "!SERVER!:/tmp/leadflow-orbita.env"
if errorlevel 1 exit /b 1

ssh !SSH_ARGS! !SERVER! "cp /tmp/leadflow-orbita.env !REMOTE_DIR!/.env && chmod 600 !REMOTE_DIR!/.env && rm -f /tmp/leadflow-orbita.env"
if errorlevel 1 exit /b 1

exit /b 0

:resolve_ssh_key
set "SSH_KEY="
if defined LEADFLOW_SSH_KEY if exist "%LEADFLOW_SSH_KEY%" set "SSH_KEY=%LEADFLOW_SSH_KEY%"
if not defined SSH_KEY if defined ORBITA_SSH_KEY if exist "%ORBITA_SSH_KEY%" set "SSH_KEY=%ORBITA_SSH_KEY%"
exit /b 0

:build_transport_args
set "SSH_ARGS=-p !LEADFLOW_SSH_PORT! -o BatchMode=yes -o StrictHostKeyChecking=accept-new"
set "SCP_ARGS=-P !LEADFLOW_SSH_PORT! -o BatchMode=yes -o StrictHostKeyChecking=accept-new"
if defined SSH_KEY (
    set "SSH_ARGS=-i !SSH_KEY! !SSH_ARGS!"
    set "SCP_ARGS=-i !SSH_KEY! !SCP_ARGS!"
)
exit /b 0

:resolve_secret_file
if defined LEADFLOW_SECRET_FILE (
    set "SECRET_FILE=%LEADFLOW_SECRET_FILE%"
) else if exist "%ROOT%secrets\orbita.env" (
    set "SECRET_FILE=%ROOT%secrets\orbita.env"
) else (
    set "SECRET_FILE=!REPO!\deploy\control-panel\.env"
)
exit /b 0

:require_command
where %~1 >nul 2>&1
if errorlevel 1 (
    echo Required command not found in PATH: %~1
    exit /b 1
)
exit /b 0

:pause_prompt
echo.
echo Press any key to return to the menu...
powershell -NoProfile -Command "$Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') > $null"
exit /b 0

:done
popd >nul
exit /b %ERRORLEVEL%