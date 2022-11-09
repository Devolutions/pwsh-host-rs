#![allow(dead_code)]

use quick_xml::events::Event;
use quick_xml::reader::Reader;
use quick_xml::events;
use uuid::Uuid;
use std::time::Duration;
use crate::time::parse_iso8601_duration;
//use std::fmt;

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

#[derive(Debug, Clone, Default)]
pub struct CliObject {
    pub name: Option<String>,
    pub values: Vec<CliValue>,
	pub ref_id: Option<String>,
    pub type_names: Vec<String>,
    pub string_repr: Option<String>
}

impl CliObject {
    pub fn new(name: Option<&str>, value: Vec<CliValue>, ref_id: Option<&str>, type_names: Vec<String>, string_repr: Option<&str>) -> CliObject {
        CliObject {
            name: name.map(|s| s.to_string()),
            values: value,
            ref_id: ref_id.map(|s| s.to_string()),
            type_names: type_names,
            string_repr: string_repr.map(|s| s.to_string()),
        }
    }
}

// Type Names (<TN>, <T>, <TNRef>)
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/2784bd9c-267d-4297-b603-722c727f85f1

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliTypeName {
    pub name: Option<String>,
    pub values: Vec<String>,
	pub ref_id: String,
}

impl CliTypeName {
    pub fn new(name: Option<&str>, value: Vec<String>, ref_id: &str) -> CliTypeName {
        CliTypeName {
            name: name.map(|s| s.to_string()),
            values: value,
            ref_id: ref_id.to_string()
        }
    }
}

// String type (<S>)
// Example: <S>This is a string</S>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/052b8c32-735b-49c0-8c24-bb32a5c871ce

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliString {
    pub value: String,
    pub name: Option<String>,
}

impl CliString {
    pub fn new(name: Option<&str>, value: &str) -> CliString {
        CliString {
            name: name.map(|s| s.to_string()),
            value: value.to_string(),
        }
    }
}

// Boolean type (<B>)
// Example: <B>true</B>
// https://www.w3.org/TR/xmlschema-2/#boolean
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/8b4b1067-4b58-46d5-b1c9-b881b6e7a0aa

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliBool {
    pub value: bool,
    pub name: Option<String>,
}

impl CliBool {
    pub fn new(name: Option<&str>, value: bool) -> CliBool {
        CliBool {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(name: Option<&str>, value: &str) -> Option<CliBool> {
        let value = value.parse::<bool>().ok()?;
        Some(Self::new(name, value))
    }
}

// GUID type (<G>)
// Example: <G>792e5b37-4505-47ef-b7d2-8711bb7affa8</G>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/c30c37fa-692d-49c7-bb86-b3179a97e106

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliGuid {
    pub value: Uuid,
    pub name: Option<String>,
}

impl CliGuid {
    pub fn new(name: Option<&str>, value: Uuid) -> CliGuid {
        CliGuid {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(name: Option<&str>, value: &str) -> Option<CliGuid> {
		let value = Uuid::parse_str(value).ok()?;
        Some(Self::new(name, value))
    }
}

// Duration type (<TS>)
// Example: <TS>PT9.0269026S</TS>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/434cd15d-8fb3-462c-a004-bcd0d3a60201

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliDuration {
    pub value: Duration,
    pub name: Option<String>,
}

impl CliDuration {
    pub fn new(name: Option<&str>, value: Duration) -> CliDuration {
        CliDuration {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(name: Option<&str>, value: &str) -> Option<CliDuration> {
        let value = parse_iso8601_duration(value)?;
        Some(Self::new(name, value))
    }
}

// Signed Byte type (<SB>)
// Example: <SB>-127</SB>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/8046c418-1531-4c43-9b9d-fb9bceace0db

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliInt8 {
    pub value: i8,
    pub name: Option<String>,
}

impl CliInt8 {
    pub fn new(name: Option<&str>, value: i8) -> CliInt8 {
        CliInt8 {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(name: Option<&str>, value: &str) -> Option<CliInt8> {
        let value = value.parse::<i8>().ok()?;
        Some(Self::new(name, value))
    }
}

// Signed Short type (<I16>)
// Example: <I16>-16767</I16>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/e0ed596d-0aea-40bb-a254-285b71188214

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliInt16 {
    pub value: i16,
    pub name: Option<String>,
}

impl CliInt16 {
    pub fn new(name: Option<&str>, value: i16) -> CliInt16 {
        CliInt16 {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(name: Option<&str>, value: &str) -> Option<CliInt16> {
        let value = value.parse::<i16>().ok()?;
        Some(Self::new(name, value))
    }
}

// Signed Int type (<I32>)
// Example: <I32>-2147483648</I32>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/9eef96ba-1876-427b-9450-75a1b28f5668

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliInt32 {
    pub value: i32,
    pub name: Option<String>,
}

impl CliInt32 {
    pub fn new(name: Option<&str>, value: i32) -> CliInt32 {
        CliInt32 {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(name: Option<&str>, value: &str) -> Option<CliInt32> {
        let value = value.parse::<i32>().ok()?;
        Some(Self::new(name, value))
    }
}

// Signed Long type (<I64>)
// Example: <I64>-9223372036854775808</I64>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/de124e86-3f8c-426a-ab75-47fdb4597c62

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliInt64 {
    pub value: i64,
    pub name: Option<String>,
}

impl CliInt64 {
    pub fn new(name: Option<&str>, value: i64) -> CliInt64 {
        CliInt64 {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(name: Option<&str>, value: &str) -> Option<CliInt64> {
        let value = value.parse::<i64>().ok()?;
        Some(Self::new(name, value))
    }
}

// Float type (<Sg>)
// Example: <Sg>12.34</Sg>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/d8a5a9ab-5f52-4175-96a3-c29afb7b82b8

#[derive(Debug, Clone, PartialEq, Default)]
pub struct CliFloat {
    pub value: f32,
    pub name: Option<String>,
}

impl CliFloat {
    pub fn new(name: Option<&str>, value: f32) -> CliFloat {
        CliFloat {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(name: Option<&str>, value: &str) -> Option<CliFloat> {
        let value = value.parse::<f32>().ok()?;
        Some(Self::new(name, value))
    }
}

// Double type (<Db>)
// Example: <Db>12.34</Db>
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/02fa08c5-139c-4e98-a13e-45784b4eabde

#[derive(Debug, Clone, PartialEq, Default)]
pub struct CliDouble {
    pub value: f64,
    pub name: Option<String>,
}

impl CliDouble {
    pub fn new(name: Option<&str>, value: f64) -> CliDouble {
        CliDouble {
            name: name.map(|s| s.to_string()),
            value: value,
        }
    }

    pub fn new_from_str(name: Option<&str>, value: &str) -> Option<CliDouble> {
        let value = value.parse::<f64>().ok()?;
        Some(Self::new(name, value))
    }
}

// Generic CLI XML Value type

#[derive(Debug, Clone)]
pub enum CliValue {
    Null,
    CliObject(CliObject),
    CliString(CliString),
    CliBool(CliBool),
	CliGuid(CliGuid),
    CliDuration(CliDuration),
    CliInt8(CliInt8),
    CliInt16(CliInt16),
    CliInt32(CliInt32),
    CliInt64(CliInt64),
    CliFloat(CliFloat),
    CliDouble(CliDouble),
}

impl CliValue {
    pub fn get_name(&self) -> Option<&str> {
        match &*self {
            CliValue::CliObject(prop) => { prop.name.as_deref() },
            CliValue::CliString(prop) => { prop.name.as_deref() },
            CliValue::CliBool(prop) => { prop.name.as_deref() },
            CliValue::CliGuid(prop) => { prop.name.as_deref() },
            CliValue::CliDuration(prop) => { prop.name.as_deref() },
            CliValue::CliInt8(prop) => { prop.name.as_deref() },
            CliValue::CliInt16(prop) => { prop.name.as_deref() },
            CliValue::CliInt32(prop) => { prop.name.as_deref() },
            CliValue::CliInt64(prop) => { prop.name.as_deref() },
            CliValue::CliFloat(prop) => { prop.name.as_deref() },
            CliValue::CliDouble(prop) => { prop.name.as_deref() },
            _ => None,
        }
    }

    pub fn is_object(&self) -> bool {
        match *self {
            CliValue::CliObject(_) => true,
            _ => false,
        }
    }

    pub fn as_object(&self) -> Option<&CliObject> {
        match &*self {
            CliValue::CliObject(prop) => Some(&prop),
            _ => None,
        }
    }

    pub fn is_string(&self) -> bool {
        match *self {
            CliValue::CliString(_) => true,
            _ => false,
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match &*self {
            CliValue::CliString(prop) => Some(&prop.value),
            _ => None,
        }
    }

    pub fn is_bool(&self) -> bool {
        match *self {
            CliValue::CliBool(_) => true,
            _ => false,
        }
    }

    pub fn as_bool(&self) -> Option<bool> {
        match &*self {
            CliValue::CliBool(prop) => Some(prop.value),
            _ => None,
        }
    }

    pub fn is_guid(&self) -> bool {
        match *self {
            CliValue::CliGuid(_) => true,
            _ => false,
        }
    }

    pub fn as_guid(&self) -> Option<Uuid> {
        match &*self {
            CliValue::CliGuid(prop) => Some(prop.value.clone()),
            _ => None,
        }
    }

    pub fn is_duration(&self) -> bool {
        match *self {
            CliValue::CliDuration(_) => true,
            _ => false,
        }
    }

    pub fn as_duration(&self) -> Option<Duration> {
        match &*self {
            CliValue::CliDuration(prop) => Some(prop.value.clone()),
            _ => None,
        }
    }

    pub fn is_int8(&self) -> bool {
        match *self {
            CliValue::CliInt8(_) => true,
            _ => false,
        }
    }

    pub fn as_int8(&self) -> Option<i8> {
        match &*self {
            CliValue::CliInt8(prop) => Some(prop.value),
            _ => None,
        }
    }

    pub fn is_int16(&self) -> bool {
        match *self {
            CliValue::CliInt16(_) => true,
            _ => false,
        }
    }

    pub fn as_int16(&self) -> Option<i16> {
        match &*self {
            CliValue::CliInt8(prop) => Some(prop.value as i16),
            CliValue::CliInt16(prop) => Some(prop.value),
            _ => None,
        }
    }

    pub fn is_int32(&self) -> bool {
        match *self {
            CliValue::CliInt32(_) => true,
            _ => false,
        }
    }

    pub fn as_int32(&self) -> Option<i32> {
        match &*self {
            CliValue::CliInt8(prop) => Some(prop.value as i32),
            CliValue::CliInt16(prop) => Some(prop.value as i32),
            CliValue::CliInt32(prop) => Some(prop.value),
            _ => None,
        }
    }

    pub fn is_int64(&self) -> bool {
        match *self {
            CliValue::CliInt64(_) => true,
            _ => false,
        }
    }

    pub fn as_int64(&self) -> Option<i64> {
        match &*self {
            CliValue::CliInt8(prop) => Some(prop.value as i64),
            CliValue::CliInt16(prop) => Some(prop.value as i64),
            CliValue::CliInt32(prop) => Some(prop.value as i64),
            CliValue::CliInt64(prop) => Some(prop.value),
            _ => None,
        }
    }

    pub fn is_float(&self) -> bool {
        match *self {
            CliValue::CliFloat(_) => true,
            _ => false,
        }
    }

    pub fn as_float(&self) -> Option<f32> {
        match &*self {
            CliValue::CliFloat(prop) => Some(prop.value),
            _ => None,
        }
    }

    pub fn is_double(&self) -> bool {
        match *self {
            CliValue::CliDouble(_) => true,
            _ => false,
        }
    }

    pub fn as_double(&self) -> Option<f64> {
        match &*self {
            CliValue::CliFloat(prop) => Some(prop.value as f64),
            CliValue::CliDouble(prop) => Some(prop.value),
            _ => None,
        }
    }
}

fn try_get_ref_id_attr<B>(reader: &Reader<B>, event: &events::BytesStart) -> Option<String> {
    let attr = event.try_get_attribute("RefId").ok().unwrap()?;
    let value = attr.decode_and_unescape_value(&reader).ok()?;
    Some(value.to_string())
}

fn try_get_name_attr<B>(reader: &Reader<B>, event: &events::BytesStart) -> Option<String> {
    let attr = event.try_get_attribute("N").ok().unwrap()?;
    let value = attr.decode_and_unescape_value(&reader).ok()?;
    Some(value.to_string())
}

pub fn parse_cli_xml(cli_xml: &str) -> Vec<CliObject> {
    let mut reader = Reader::from_str(cli_xml);
    reader.trim_text(true);

    let mut objs: Vec<CliObject> = Vec::new();
    let mut obj = CliObject::default();

    loop {
        let event = reader.read_event();
        match event {
            Ok(Event::Start(event)) => {
                //let event_name = event.name();
                //let tag_name = String::from_utf8_lossy(event_name.as_ref());
                //println!("Enter: {}", &tag_name);

                match event.name().as_ref() {
                    b"Objs" => {

                    },
                    b"Obj" => {
                        if let Some(ref_id) = try_get_ref_id_attr(&reader, &event) {
                            obj.ref_id = Some(ref_id);
                        }
                    },
                    b"TN" => {
                        if let Some(_ref_id) = try_get_ref_id_attr(&reader, &event) {
                            //println!("TN RefId={}", ref_id);
                        }
                    },
                    b"T" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        obj.type_names.push(txt.to_string());
                    },
                    b"TNRef" => {
                        if let Some(_ref_id) = try_get_ref_id_attr(&reader, &event) {
                            //println!("TNRef RefId={}", ref_id);
                        }
                    },
                    b"Props" => {
                        // Adapted Properties
                        // https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/173c30d7-b0a6-4aad-9b00-9891c441b0f3
                    },
                    b"MS" => {
                        // Extended Properties
                        // https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-psrp/4cca6d92-4a8e-4406-91cb-0235a98f7d6f
                    },
                    b"B" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event);
                        let val = CliBool::new_from_str(prop_name.as_deref(), &txt).unwrap();
                        obj.values.push(CliValue::CliBool(val));
                    },
                    b"S" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event);
                        let val = CliString::new(prop_name.as_deref(), &txt);
                        obj.values.push(CliValue::CliString(val));
                    },
                    b"G" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event);
                        let val = CliGuid::new_from_str(prop_name.as_deref(), &txt).unwrap();
                        obj.values.push(CliValue::CliGuid(val));
                    },
                    b"ToString" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        obj.string_repr = Some(txt.to_string());
                    },
                    b"SB" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event);
                        let val = CliInt8::new_from_str(prop_name.as_deref(), &txt).unwrap();
                        obj.values.push(CliValue::CliInt8(val));
                    },
                    b"I16" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event);
                        let val = CliInt16::new_from_str(prop_name.as_deref(), &txt).unwrap();
                        obj.values.push(CliValue::CliInt16(val));
                    },
                    b"I32" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event);
                        let val = CliInt32::new_from_str(prop_name.as_deref(), &txt).unwrap();
                        obj.values.push(CliValue::CliInt32(val));
                    },
                    b"I64" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event);
                        let val = CliInt64::new_from_str(prop_name.as_deref(), &txt).unwrap();
                        obj.values.push(CliValue::CliInt64(val));
                    },
                    b"TS" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event);
                        let val = CliDuration::new_from_str(prop_name.as_deref(), &txt).unwrap();
                        obj.values.push(CliValue::CliDuration(val));
                    },
                    b"Db" => {
                        let txt = reader.read_text(event.name()).unwrap();
                        let prop_name = try_get_name_attr(&reader, &event);
                        let val = CliDouble::new_from_str(prop_name.as_deref(), &txt).unwrap();
                        obj.values.push(CliValue::CliDouble(val));
                    },
                    b"Nil" => {
                        let _val = CliValue::Null; // null value
                    },
                    _ => {
                        let event_name = event.name();
                        let tag_name = String::from_utf8_lossy(event_name.as_ref());
                        println!("unsupported: {}", &tag_name);
                    }
                }
            },
            Ok(Event::End(event)) => {
                match event.name().as_ref() {
                    b"Obj" => {
                        objs.push(obj);
                        obj = CliObject::default();
                    },
                    _ => { }
                }
            },
            Ok(Event::Eof) => break,
            Err(event) => panic!("Error at position {}: {:?}", reader.buffer_position(), event),
            _ => (),
        }
    }

    objs
}
