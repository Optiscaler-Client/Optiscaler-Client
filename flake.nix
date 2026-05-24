{
  inputs = {
    utils.url = "github:numtide/flake-utils";
  };
  outputs =
    {
      self,
      nixpkgs,
      utils,
    }:
    utils.lib.eachDefaultSystem (
      system:
      let
        pkgs = nixpkgs.legacyPackages.${system};
        dotnet = pkgs.dotnetCorePackages.dotnet_10;
        dotnet-sdk = dotnet.sdk;
        dotnet-runtime = dotnet.runtime;
      in
      rec {
        devShell = pkgs.mkShell {
          buildInputs = with pkgs; [
            dotnet-sdk
            fontconfig.lib
            skia
            pkg-config
            libx11
            libice
            nuget-to-json
          ];

          LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath (
            with pkgs;
            [
              fontconfig.lib
              libx11
              libice
              libsm
            ]
          );
        };
        packages.default = pkgs.buildDotnetModule rec {
          pname = "OptiscalerClient";
          version = "1.0.5";
          src = ./.;

          inherit dotnet-sdk dotnet-runtime;
          projectFile = "OptiscalerClient.csproj";
          nugetDeps = ./nix-deps.json;
          dotnetInstallFlags = "-p:EnableCompressionInSingleFile=false";

          postFixup = ''
            mkdir -p $out/share/icons/hicolor/256x256/apps/
            cp -r $src/assets/icon.png $out/share/icons/hicolor/256x256/apps/${meta.mainProgram}.png
          '';

          nativeBuildInputs = [
            pkgs.copyDesktopItems
          ];

          desktopItems = [
            (pkgs.makeDesktopItem {
              name = meta.mainProgram;
              exec = meta.mainProgram;
              icon = meta.mainProgram;
              desktopName = meta.mainProgram;
              genericName = "Optiscaler Client";
              comment = meta.description;
              type = "Application";
              categories = [ "Utility" ];
              startupNotify = true;
            })
          ];

          meta = with pkgs; {
            description = "A modern manager for OptiScaler";
            homepage = "https://github.com/Agustinm28/Optiscaler-Client";
            license = lib.licenses.gpl3Only;
            platforms = lib.platforms.linux;
            mainProgram = "OptiscalerClient";
          };
        };
      }
    );
}
