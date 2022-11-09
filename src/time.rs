
pub fn parse_iso8601_duration(input: &str) -> Option<std::time::Duration> {
    iso8601_duration::Duration::parse(input).ok().map(|x| x.to_std())
}

#[cfg(test)]
mod pwsh {
    use crate::time::parse_iso8601_duration;

    #[test]
    fn parse_duration() {
        // 0 seconds
        assert_eq!(parse_iso8601_duration("PT0S"),
            Some(std::time::Duration::new(0, 0)));

        // 9 seconds, 26.9026 milliseconds
        assert_eq!(parse_iso8601_duration("PT9.0269026S"),
            Some(std::time::Duration::from_secs_f32(9.0269026)));
    }
}
