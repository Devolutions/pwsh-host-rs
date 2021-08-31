mod context;
mod delegate_loader;
mod detect;
mod error;
mod host_detect;
mod host_exit_code;
mod hostfxr;
mod ipwsh;

extern crate libc;
#[macro_use]
extern crate dlopen_derive;
extern crate dlopen;
#[macro_use]
extern crate quick_error;

/// Module for a platform dependent c-like string type.
#[macro_use]
mod pdcstring;
