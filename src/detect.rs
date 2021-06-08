extern "C" {
    pub fn pwsh_host_detect() -> bool;
    pub fn pwsh_host_app() -> bool;
    pub fn pwsh_host_lib() -> bool;
}

pub fn my_host_detect() {
    unsafe { let _ = pwsh_host_detect(); }
}

#[cfg(test)]
mod tests {
    use crate::detect::*;

    #[test]
    fn host_detect() {
        unsafe { let _ = pwsh_host_detect(); }
    }

    #[test]
    fn host_app() {
        unsafe { let _ = pwsh_host_lib(); }
    }

    #[test]
    fn host_lib() {
        unsafe { let _ = pwsh_host_lib(); }
    }
}
