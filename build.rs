use cmake::Config;
use std::env;
use std::path::PathBuf;

fn main() {
    let profile = env::var("PROFILE").unwrap();
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let cmake_source_dir = manifest_dir.clone();
    let mut cmake_binary_dir = PathBuf::from(&out_dir);
    cmake_binary_dir.push("build");

    println!("OUT_DIR {}", &out_dir.to_str().unwrap());
    println!("MANIFEST_DIR {}", &manifest_dir.to_str().unwrap());
    println!("CMAKE_SOURCE_DIR: {}", &cmake_source_dir.to_str().unwrap());
    println!("CMAKE_BINARY_DIR: {}", &cmake_binary_dir.to_str().unwrap());

    let cmake_build_type = if profile == "debug" {
        "Debug"
    } else {
        "Release"
    };

    let mut cmake_config = Config::new(&cmake_source_dir);
    cmake_config.generator("Ninja").profile(cmake_build_type);

    if cfg!(windows) {
        cmake_config
            .define("CMAKE_C_FLAGS", "/nologo")
            .define("CMAKE_CXX_FLAGS", "/nologo")
            .static_crt(true);
    } else if cfg!(macos) {
        cmake_config.define("CMAKE_OSX_DEPLOYMENT_TARGET", "10.9");
    }

    let cmake_output = cmake_config.build();

    println!(
        "cargo:rustc-link-search=native={}",
        cmake_output.join("build\\lib").display()
    );
    println!("cargo:rustc-link-lib=static=resources");
    println!("cargo:rerun-if-changed=build.rs");
}
