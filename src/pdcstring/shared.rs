use super::{NulError, PdCStrInner, PdCStringInner, PdUChar};
use std::{
    borrow::Borrow,
    convert::TryFrom,
    fmt::{self, Debug, Display, Formatter},
    ops::Deref,
    str::FromStr,
};

/// A platform-dependent c-like string type for interacting with the hostfxr API.
#[derive(Debug, PartialEq, Eq, PartialOrd, Ord, Hash, Clone)]
#[repr(transparent)]
pub struct PdCString(pub PdCStringInner);

impl PdCString {
    pub fn from_inner(inner: PdCStringInner) -> Self {
        Self(inner)
    }
    pub fn into_inner(self) -> PdCStringInner {
        self.0
    }
}

/// A borrowed slice of a PdCString.
#[derive(Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
#[repr(transparent)]
pub struct PdCStr(pub PdCStrInner);

impl PdCStr {
    pub fn from_inner(inner: &PdCStrInner) -> &Self {
        // Safety:
        // Safe because PdCStr has the same layout as PdCStrInner
        unsafe { &*(inner as *const PdCStrInner as *const PdCStr) }
    }

    pub fn as_inner(&self) -> &PdCStrInner {
        // Safety:
        // Safe because PdCStr has the same layout as PdCStrInner
        unsafe { &*(self as *const PdCStr as *const PdCStrInner) }
    }
}

impl Borrow<PdCStr> for PdCString {
    fn borrow(&self) -> &PdCStr {
        PdCStr::from_inner(self.0.borrow())
    }
}

impl AsRef<PdCStr> for PdCString {
    fn as_ref(&self) -> &PdCStr {
        self.borrow()
    }
}

impl Deref for PdCString {
    type Target = PdCStr;
    fn deref(&self) -> &Self::Target {
        self.borrow()
    }
}

impl Default for PdCString {
    fn default() -> Self {
        Self(Default::default())
    }
}

impl Display for PdCStr {
    fn fmt(&self, f: &mut Formatter<'_>) -> fmt::Result {
        self.0.fmt(f)
    }
}

impl From<PdCStringInner> for PdCString {
    fn from(s: PdCStringInner) -> Self {
        Self::from_inner(s)
    }
}

impl From<PdCString> for PdCStringInner {
    fn from(s: PdCString) -> Self {
        s.into_inner()
    }
}

impl<'a> From<&'a PdCStrInner> for &'a PdCStr {
    fn from(s: &'a PdCStrInner) -> Self {
        PdCStr::from_inner(s)
    }
}

impl<'a> From<&'a PdCStr> for &'a PdCStrInner {
    fn from(s: &'a PdCStr) -> Self {
        s.as_inner()
    }
}

impl<'a> TryFrom<&'a str> for PdCString {
    type Error = NulError;

    fn try_from(s: &'a str) -> Result<Self, Self::Error> {
        Self::from_str(s)
    }
}

impl TryFrom<Vec<PdUChar>> for PdCString {
    type Error = NulError;

    fn try_from(vec: Vec<PdUChar>) -> Result<Self, Self::Error> {
        Self::from_vec(vec)
    }
}

impl From<PdCString> for Vec<PdUChar> {
    fn from(s: PdCString) -> Vec<PdUChar> {
        s.into_vec()
    }
}

impl AsRef<PdCStr> for PdCStr {
    fn as_ref(&self) -> &Self {
        self
    }
}

impl ToOwned for PdCStr {
    type Owned = PdCString;
    fn to_owned(&self) -> Self::Owned {
        PdCString::from_inner(self.0.to_owned())
    }
}
