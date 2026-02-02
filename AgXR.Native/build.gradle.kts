plugins {
    id("com.android.library") version "8.4.1"
}

android {
    namespace = "com.agxr.native_lib"
    compileSdk = 35
    ndkVersion = "27.0.12077973" // Adjust based on installed NDK

    defaultConfig {
        minSdk = 29 // Android 10+
        
        externalNativeBuild {
            cmake {
                cppFlags("-std=c++20")
                arguments("-DANDROID_STL=c++_shared")
            }
        }
    }

    externalNativeBuild {
        cmake {
            path("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }
    
    // ARCore Geospatial requires specific configs but mainly in App Manifest
    // Jetpack XR SDK deps would go here if we were doing Java/Kotlin wrappers
}

dependencies {
    // Add Jetpack XR SDK if needed
    // implementation("androidx.xr.scenecore:scenecore:1.0.0-alpha01")
}
