# Include after liberafx_runtime is declared. No glob: one implementation per node.
target_sources(liberafx_runtime PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionMath.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbRandomMath.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionRandomNodes.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionIdentityNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionComposeNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionNormalizeNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionConjugateNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionInverseNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionMultiplyNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionFromEulerNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionToEulerNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionAxisAngleNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionSlerpNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbQuaternionRotateVectorNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbRandomFloatNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbRandomIntNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbRandomVector3Node.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbRandomUnitSphereNode.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbRandomInSphereNode.cpp)
