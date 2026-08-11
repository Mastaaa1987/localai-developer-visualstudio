# Visual Studio Marketplace release

The Marketplace publisher ID is `localai-developer`. The VSIX identity ID must remain unchanged so installed versions receive updates correctly.

## Build and test

1. Run the backend test suite:

   ```powershell
   dotnet run --project .\tests\LocalAI.Developer.Backend.Tests\LocalAI.Developer.Backend.Tests.csproj -c Release
   ```

2. Rebuild the Release VSIX with Visual Studio MSBuild:

   ```powershell
   & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
     .\src\LocalAI.Developer.VisualStudio\LocalAI.Developer.VisualStudio.csproj `
     /t:Rebuild /p:Configuration=Release /p:DeployExtension=false
   ```

3. Install the Release VSIX locally and verify provider settings, model discovery, plan creation, patch review, transaction rollback, final validation, and session reload.

The local development previews used a different manifest publisher value. Uninstall an installed 1.3.x preview before testing the Marketplace-ready 1.4.0 package to avoid side-by-side identity or update conflicts.

## First private upload

`vs-publish.json` intentionally uses `"private": true` for the first Marketplace upload. This allows verification before public release.

Create the publisher at <https://marketplace.visualstudio.com/manage>, then publish with the VSSDK tool:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe" login `
  -publisherName "localai-developer" `
  -personalAccessToken "<MARKETPLACE_PAT>"

& "C:\Program Files\Microsoft Visual Studio\2022\Community\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe" publish `
  -payload ".\src\LocalAI.Developer.VisualStudio\bin\Release\LocalAI.Developer.VisualStudio.vsix" `
  -publishManifest ".\vs-publish.json"
```

Never commit the Marketplace personal access token. After the private listing has been tested, change `private` to `false` and publish the same version again or make the listing public in the Marketplace management portal.
