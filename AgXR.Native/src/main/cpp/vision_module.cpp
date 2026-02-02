#include <jni.h>
#include <string>
#include <android/log.h>

#define LOG_TAG "AgXR_Native"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)

extern "C" JNIEXPORT jstring JNICALL
Java_com_agxr_native_1lib_NativeLib_stringFromJNI(
        JNIEnv* env,
        jobject /* this */) {
    std::string hello = "Hello from AgXR Native Edge AI (C++)";
    LOGI("Native method called: stringFromJNI");
    return env->NewStringUTF(hello.c_str());
}

// TODO: Add Edge AI Vision logic here (e.g., TFLite, MediaPipe, or custom models)
// TODO: Add functions to process ARCore Geospatial Anchors using NDK
