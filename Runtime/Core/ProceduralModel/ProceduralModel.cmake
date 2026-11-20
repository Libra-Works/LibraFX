target_sources(liberafx_runtime PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/LbProceduralModel.cpp
    ${CMAKE_CURRENT_LIST_DIR}/LbProceduralPixel.cpp
    ${CMAKE_CURRENT_LIST_DIR}/ProceduralModelValidation.cpp
    ${CMAKE_CURRENT_LIST_DIR}/ProceduralModelShapes.cpp
    ${CMAKE_CURRENT_LIST_DIR}/ProceduralModelBaseTopology.cpp
    ${CMAKE_CURRENT_LIST_DIR}/ProceduralModelTopology.cpp
    ${CMAKE_CURRENT_LIST_DIR}/ProceduralModelNormals.cpp
    ${CMAKE_CURRENT_LIST_DIR}/ProceduralModelEvaluation.cpp)
target_compile_definitions(liberafx_runtime PUBLIC LIBRA_PROCEDURAL_STATIC=1)
