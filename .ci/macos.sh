#!/bin/sh

# Install build tools
brew install ninja create-dmg

CMakeNinjaBuild() {
	# One time for x64
	mkdir build_$1_shared_osx-x64
	cd build_$1_shared_osx-x64
	cmake ../../GSE/externals/$1 \
		-DCMAKE_BUILD_TYPE=Release \
		-DCMAKE_C_COMPILER=clang \
		-DCMAKE_CXX_COMPILER=clang++ \
		-DCMAKE_OBJC_COMPILER=clang \
		-DCMAKE_OBJCXX_COMPILER=clang++ \
		-DCMAKE_OSX_ARCHITECTURES=x86_64 \
		-DCMAKE_SYSTEM_NAME=Darwin \
		-DCMAKE_SYSTEM_PROCESSOR=x86_64 \
		-G Ninja \
		-DGSE_SHARED=ON
	ninja
	cd ..
	# Another time for arm64
	mkdir build_$1_shared_osx-arm64
	cd build_$1_shared_osx-arm64
	cmake ../../GSE/externals/$1 \
		-DCMAKE_BUILD_TYPE=Release \
		-DCMAKE_C_COMPILER=clang \
		-DCMAKE_CXX_COMPILER=clang++ \
		-DCMAKE_OBJC_COMPILER=clang \
		-DCMAKE_OBJCXX_COMPILER=clang++ \
		-DCMAKE_OSX_ARCHITECTURES=arm64 \
		-DCMAKE_SYSTEM_NAME=Darwin \
		-DCMAKE_SYSTEM_PROCESSOR=arm64 \
		-G Ninja \
		-DGSE_SHARED=ON
	ninja
	cd ..
}

CMakeNinjaBuild SDL2
CMakeNinjaBuild gambatte
CMakeNinjaBuild mgba

# Build InputLogPlayer
cd ..
dotnet publish -r osx-x64
dotnet publish -r osx-arm64

# Abort if the build failed for whatever reason
if [ ! -f output/osx-x64/publish/InputLogPlayer ] || [ ! -f output/osx-arm64/publish/InputLogPlayer ]; then
	echo "dotnet publish failed, aborting"
	exit 1
fi

# Create .app bundle structure
mkdir output/$TARGET_RID
mkdir output/$TARGET_RID/InputLogPlayer.app
mkdir output/$TARGET_RID/InputLogPlayer.app/Contents
mkdir output/$TARGET_RID/InputLogPlayer.app/Contents/MacOS

# Merge the binaries together
lipo output/osx-x64/publish/InputLogPlayer output/osx-arm64/publish/InputLogPlayer -create -output output/$TARGET_RID/InputLogPlayer.app/Contents/MacOS/InputLogPlayer
lipo output/osx-x64/publish/libSDL2.dylib output/osx-arm64/publish/libSDL2.dylib -create -output output/$TARGET_RID/InputLogPlayer.app/Contents/MacOS/libSDL2.dylib
lipo output/osx-x64/publish/libgambatte.dylib output/osx-arm64/publish/libgambatte.dylib -create -output output/$TARGET_RID/InputLogPlayer.app/Contents/MacOS/libgambatte.dylib
lipo output/osx-x64/publish/libmgba.dylib output/osx-arm64/publish/libmgba.dylib -create -output output/$TARGET_RID/InputLogPlayer.app/Contents/MacOS/libmgba.dylib

# Add in Info.plist
cp InputLogPlayer/Info.plist output/$TARGET_RID/InputLogPlayer.app/Contents

# Resign the binary
codesign -s - --deep --force output/$TARGET_RID/InputLogPlayer.app

# Output a dmg
mkdir output/$TARGET_RID/publish
create-dmg output/$TARGET_RID/publish/InputLogPlayer.dmg output/$TARGET_RID/InputLogPlayer.app
