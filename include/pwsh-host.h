#ifndef PWSH_HOST_API_H
#define PWSH_HOST_API_H

#if defined _WIN32 || defined __CYGWIN__
	#ifdef PWSH_HOST_EXPORTS
		#ifdef __GNUC__
			#define PWSH_HOST_EXPORT __attribute__((dllexport))
		#else
			#define PWSH_HOST_EXPORT __declspec(dllexport)
		#endif
	#else
		#ifdef __GNUC__
			#define PWSH_HOST_EXPORT __attribute__((dllimport))
		#else
			#define PWSH_HOST_EXPORT __declspec(dllimport)
		#endif
	#endif
#else
	#if __GNUC__ >= 4
		#define PWSH_HOST_EXPORT   __attribute__ ((visibility("default")))
	#else
		#define PWSH_HOST_EXPORT
	#endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

PWSH_HOST_EXPORT bool pwsh_host_detect();
PWSH_HOST_EXPORT bool pwsh_host_app();
PWSH_HOST_EXPORT bool pwsh_host_lib();

#ifdef __cplusplus
}
#endif

#endif /* PWSH_HOST_API_H */