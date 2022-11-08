use quick_xml::events::Event;
use quick_xml::reader::Reader;
use quick_xml::events;
use uuid::Uuid;
use std::fmt;

// [MS-PSRP]: PowerShell Remoting Protocol
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp

// [XMLSCHEMA2]: XML Schema Part 2: Datatypes Second Edition
// https://www.w3.org/TR/xmlschema-2/

// Serialization of Primitive Type Objects:
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/c8c85974-ffd7-4455-84a8-e49016c20683

// Serialization of Complex Objects:
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/406ad572-1ede-43e0-b063-e7291cda3e63

// Object type (<Obj>)
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/3e107e78-3f28-4f85-9e25-493fd9b09726

#[derive(Debug, Clone)]
pub struct CliObject {
    pub value: Vec<CliValue>,
    pub name: Option<String>,
}

impl CliObject {
    pub fn new(value: Vec<CliValue>, name: Option<&str>) -> CliObject {
        CliObject {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }
}

impl fmt::Display for CliObject {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        write!(f, "Obj")
    }
}

// String type (<S>)
// Example: <S>This is a string</S>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/052b8c32-735b-49c0-8c24-bb32a5c871ce

#[derive(Debug, Clone)]
pub struct CliString {
    pub value: String,
    pub name: Option<String>,
}

impl CliString {
    pub fn new(value: &str, name: Option<&str>) -> CliString {
        CliString {
            name: name.map(|s| s.to_string()),
            value: value.to_string(),
        }
    }
}

impl fmt::Display for CliString {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        fmt::Display::fmt(self.value.as_str(), f)
    }
}

// Signed Int type (<I32>)
// Example: <I32>-2147483648</I32>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/9eef96ba-1876-427b-9450-75a1b28f5668

#[derive(Debug, Clone)]
pub struct CliInt32 {
    pub value: i32,
    pub name: Option<String>,
}

impl CliInt32 {
    pub fn new(value: i32, name: Option<&str>) -> CliInt32 {
        CliInt32 {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(value: &str, name: Option<&str>) -> Option<CliInt32> {
        let value = value.parse::<i32>().ok()?;
        Some(Self::new(value, name))
    }
}

impl fmt::Display for CliInt32 {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        fmt::Display::fmt(&self.value, f)
    }
}

// GUID type (<G>)
// Example: <G>792e5b37-4505-47ef-b7d2-8711bb7affa8</G>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/c30c37fa-692d-49c7-bb86-b3179a97e106

#[derive(Debug, Clone)]
pub struct CliGuid {
    pub value: Uuid,
    pub name: Option<String>,
}

impl CliGuid {
    pub fn new(value: Uuid, name: Option<&str>) -> CliGuid {
        CliGuid {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(value: &str, name: Option<&str>) -> Option<CliGuid> {
		let value = Uuid::parse_str(value).ok()?;
        Some(Self::new(value, name))
    }
}

impl fmt::Display for CliGuid {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        fmt::Display::fmt(&self.value, f)
    }
}

// Generic CLI XML Value type

#[derive(Debug, Clone)]
pub enum CliValue {
    Null,
    CliString(CliString),
	CliGuid(CliGuid),
    CliInt32(CliInt32),
}

impl CliValue {

}

fn try_get_ref_id_attr<B>(reader: &Reader<B>, event: &events::BytesStart) -> Option<i32> {
    let attr = event.try_get_attribute("RefId").ok().unwrap()?;
    let value = attr.decode_and_unescape_value(&reader).ok()?;
    value.parse::<i32>().ok()
}

fn try_get_name_attr<B>(reader: &Reader<B>, event: &events::BytesStart) -> Option<String> {
    let attr = event.try_get_attribute("N").ok().unwrap()?;
    let value = attr.decode_and_unescape_value(&reader).ok()?;
    Some(value.to_string())
}

pub fn parse_cli_xml(cli_xml: &str) {
    let mut reader = Reader::from_str(cli_xml);
    reader.trim_text(true);

    loop {
        let event = reader.read_event();
        match event {
            Ok(Event::Start(event)) => {
                let event_name = event.name();
                let tag_name = String::from_utf8_lossy(event_name.as_ref());
                println!("Enter: {}", &tag_name);

                match event.name().as_ref() {
                    b"Objs" => {

                    },
                    b"Obj" => {
                        let ref_id = try_get_ref_id_attr(&reader, &event).unwrap();
                        println!("Obj RefId={}", ref_id);
                    },
                    b"TN" => {
                        let ref_id = try_get_ref_id_attr(&reader, &event).unwrap();
                        println!("TN RefId={}", ref_id);
                    },
                    b"T" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        println!("T: {}", txt);
                    },
                    b"MS" => {

                    },
                    b"G" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event).unwrap();
                        let val = CliGuid::new_from_str(&txt, Some(&prop_name)).unwrap();
                        println!("G: {:?}", val);
                    },
                    b"S" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event).unwrap();
                        let val = CliString::new(&txt, Some(&prop_name));
                        println!("S: {:?}", val);
                    },
                    b"ToString" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        println!("ToString: {}", txt);
                    },
                    b"I32" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let val = CliInt32::new_from_str(&txt, None).unwrap();
                        println!("I32: {}", val.value);
                    },
                    b"TS" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        println!("TS: {}", txt);
                    },
                    b"Nil" => {
                        let _val = CliValue::Null; // null value
                    },
                    _ => { }
                }
            },
            Ok(Event::End(event)) => {
                let event_name = event.name();
                let tag_name = String::from_utf8_lossy(event_name.as_ref());
                println!("Leave: {}", &tag_name);
            },
            Ok(Event::Eof) => break,
            Err(event) => panic!("Error at position {}: {:?}", reader.buffer_position(), event),
            _ => (),
        }
    }
}
