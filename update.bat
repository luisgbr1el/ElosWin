@echo off
echo Baixando versao mais recente...
curl -L -o Elos-update.zip "https://github.com/luisgbr1el/ElosWin/releases/latest/download/Elos-win-x64.zip"
echo Extraindo arquivos...
tar -xf Elos-update.zip
del Elos-update.zip
echo Atualizacao concluida!
pause