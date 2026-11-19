import shutil,subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[1]
source=root/"compat/source"
output=root/"compat/build-output"
subprocess.run(["dotnet","publish",str(source/"src/Galope.Sdk/Galope.Sdk.csproj"),"-c","Release","-r","linux-arm64","--self-contained","false","-o",str(output)],check=True)
overlay=root/"overlay/engine"; overlay.mkdir(parents=True,exist_ok=True)
for name in ["Galope.Sdk.dll","Galope.Sdk.deps.json","Galope.Sdk.runtimeconfig.json","R36S.A11y.dll"]: shutil.copyfile(output/name,overlay/name)
