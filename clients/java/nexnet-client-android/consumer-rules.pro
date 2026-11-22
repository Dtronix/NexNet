# ProGuard/R8 rules applied to apps that use nexnet-client-android (consumerProguardFiles).
#
# Intentionally empty: the NexNet runtime uses no reflection (generated code calls it directly), so R8 may shrink,
# optimize and obfuscate it like application code. JSpecify annotations are compile-only; R8 ignores missing
# annotation classes.
#
# Add rules here only for a class the runtime looks up by name.
