#!/bin/sh

# This script expects to be running on Debian 11 under root

# Install some base tools
apt-get install -y wget lsb-release software-properties-common gpg ninja-build pkg-config

# Install clang 21
wget https://apt.llvm.org/llvm.sh -O $HOME/llvm.sh
chmod +x $HOME/llvm.sh
$HOME/llvm.sh 21

# Enable backports packages
echo "deb http://archive.debian.org/debian bullseye-backports main" | tee /etc/apt/sources.list.d/backports.list
apt-get update

# Normally cmake from standard bulleye packages is enough
# However, a bug seems to have been recently introduced in this package causing build failures for linux-x64
# The bug has cmake checks for x86_64-pc-linux-gnu paths instead of x86_64-linux-gnu paths
# bullseye-backports has a newer cmake version which has this bug fixed
apt-get install -y cmake/bullseye-backports

if [ $TARGET_RID = "linux-x64" ]; then
	# Nothing special needed here
	export EXTRA_CMAKE_ARGS=""
	# Install SDL3 dependencies
	apt-get install -y gnome-desktop-testing libasound2-dev libpulse-dev \
		libaudio-dev libfribidi-dev libjack-jackd2-dev libsndio-dev libx11-dev \
		libxext-dev libxrandr-dev libxcursor-dev libxfixes-dev libxi-dev libxss-dev \
		libxtst-dev libwayland-dev libxkbcommon-dev libdrm-dev libgbm-dev libgl1-mesa-dev \
		libgles2-mesa-dev libegl1-mesa-dev libdbus-1-dev libibus-1.0-dev \
		fcitx-libs-dev libudev-dev libusb-1.0-0-dev liburing-dev libthai-dev pkg-config
	# More SDL3 dependencies only under backports
	apt-get install -y libdecor-0-dev/bullseye-backports libpipewire-0.3-dev/bullseye-backports
	# Install .NET AOT dependencies
	apt-get install -y zlib1g-dev
elif [ $TARGET_RID = "linux-arm64" ]; then
	# Install aarch64 cross compiling setup
	apt-get install -y gcc-aarch64-linux-gnu g++-aarch64-linux-gnu dpkg-dev
	# Setup pkg-config for cross compiling
	ln -s /usr/share/pkg-config-crosswrapper /usr/bin/aarch64-linux-gnu-pkg-config
	chmod +x /usr/bin/aarch64-linux-gnu-pkg-config
	export PKG_CONFIG=aarch64-linux-gnu-pkg-config
	# cmake cross compiler flags
	export EXTRA_CMAKE_ARGS="-DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64 -DCMAKE_C_FLAGS=--target=aarch64-linux-gnu -DCMAKE_CXX_FLAGS=--target=aarch64-linux-gnu"
	# Enable ARM64 packages
	dpkg --add-architecture arm64
	apt-get update
	# Install SDL3 dependencies
	apt-get install -y gnome-desktop-testing:arm64 libasound2-dev:arm64 libpulse-dev:arm64 libaudio-dev:arm64 \
		libfribidi-dev:arm64 libjack-jackd2-dev:arm64 libsndio-dev:arm64 libx11-dev:arm64 libxext-dev:arm64 \
		libxrandr-dev:arm64 libxcursor-dev:arm64 libxfixes-dev:arm64 libxi-dev:arm64 libxss-dev:arm64 libxtst-dev:arm64 \
		libwayland-dev:arm64 libxkbcommon-dev:arm64 libdrm-dev:arm64 libgbm-dev:arm64 libgl1-mesa-dev:arm64 \
		libgles2-mesa-dev:arm64 libegl1-mesa-dev:arm64 libdbus-1-dev:arm64 libibus-1.0-dev:arm64 \
		fcitx-libs-dev:arm64 libudev-dev:arm64 libusb-1.0-0-dev:arm64 liburing-dev:arm64 libthai-dev:arm64
	# More SDL3 dependencies only under backports
	apt-get install -y libdecor-0-dev:arm64/bullseye-backports libpipewire-0.3-dev:arm64/bullseye-backports
	# Install .NET AOT dependencies
	apt-get install -y zlib1g-dev:arm64
else
	echo "TARGET_RID must be linux-x64 or linux-arm64 or linux-arm (got $TARGET_RID)"
	exit 1
fi

CMakeNinjaBuild() {
	mkdir build_$1_shared_$TARGET_RID
	cd build_$1_shared_$TARGET_RID
	cmake ../../GSE/externals/$1 \
		-DCMAKE_BUILD_TYPE=Release \
		-DCMAKE_C_COMPILER=clang-21 \
		-DCMAKE_CXX_COMPILER=clang++-21 \
		$EXTRA_CMAKE_ARGS \
		-G Ninja \
		-DGSE_SHARED=ON
	ninja
	cd ..
}

CMakeNinjaBuild SDL3
CMakeNinjaBuild gambatte

# Seems mGBA build is broken for 0.5, due to CMAKE_C_EXTENSIONS being set OFF when it should be ON
# Workaround it like so
if [ $TARGET_RID = "linux-x64" ]; then
	export EXTRA_CMAKE_ARGS="-DCMAKE_C_FLAGS=-D_GNU_SOURCE"
else # "linux-arm64"
	export EXTRA_CMAKE_ARGS="-DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64 -DCMAKE_C_FLAGS=--target=aarch64-linux-gnu;-D_GNU_SOURCE -DCMAKE_CXX_FLAGS=--target=aarch64-linux-gnu"
fi

CMakeNinjaBuild mgba

# Install dotnet9 sdk
wget https://dot.net/v1/dotnet-install.sh -O $HOME/dotnet-install.sh
chmod +x $HOME/dotnet-install.sh
$HOME/dotnet-install.sh --channel 9.0
export PATH=$HOME/.dotnet:$PATH

# Build InputLogPlayer
cd ..
dotnet publish -r $TARGET_RID -p:CppCompilerAndLinker=clang-21 -p:LinkerFlavor=lld-21 -p:ObjCopyName=llvm-objcopy-21
