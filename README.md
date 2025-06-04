# NoaChess

[Spanish below]

## Advanced Chess Engine in C# (.NET 8)

NoaChess is a modular, high-performance chess engine built with professional standards. It includes complete chess logic, move generation, advanced search and evaluation, and an API layer for integration with graphical interfaces or external clients.

### **Main Features**
- C# 12, .NET 8, cross-platform.
- Modular architecture, SOLID principles and Clean Architecture.
- Legal move generation and validation (FIDE rules).
- Search algorithms (Minimax, Alpha-Beta, roadmap: Pruning, NNUE, etc.).
- Multi-factor position evaluation (material, mobility, king safety, ...).
- REST API for GUI integration (upcoming).
- Automated test suite, TDD and high coverage (in roadmap).
- Documentation and ready-to-extend codebase.

### **Architecture**
Project is split into independent modules:
- **Core**: Chess logic, rules, move generation and validation.
- **Search**: Implementation of search algorithms and heuristics.
- **Evaluation**: Position evaluation system.
- **API**: Controllers and endpoints for external communication (REST).
- **UI/Client**: (Future) GUIs or connectors for UCI/XBoard protocols.
- **Tests**: Unit and integration tests.

Follows Clean Architecture and DDD for maintainability and scalability.

### **Technologies**
- **Language**: C# 12, .NET 8 LTS
- **IDE**: Visual Studio Code + professional extensions
- **Testing**: xUnit, FluentAssertions, Moq
- **API**: ASP.NET Core WebAPI (future)
- **CI/CD**: GitHub Actions (roadmap)
- **Performance**: BenchmarkDotNet, .NET profiling tools

### **Roadmap & Versions**
Development follows an incremental roadmap:

- **v0.1:** Basic console, shows legal moves.
- **v0.2:** Adds basic search engine and simple evaluation.
- **v0.3:** Modular architecture, clear separation of concerns.
- **v1.0:** Full engine, API integration and first E2E tests.
- **Future:** UCI/XBoard protocol, open for contributions, external GUI integration.

This README will be updated regularly as development progresses.

### **Contributing**
Contributions, suggestions, and feedback are welcome.  
Please check the [Contribution Guide](CONTRIBUTING.md) (to be added) and open issues or pull requests.

### License
This project is licensed under the **Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0)** license.
**Use, copy, modification, and redistribution are allowed for any NON-commercial purpose.**
**Any commercial use is strictly prohibited, except by the author or co-authors of the project.**
For other uses, please contact the owner.

---

## Motor de ajedrez avanzado en C# (.NET 8)

NoaChess es un motor de ajedrez modular, diseñado para alto rendimiento y extensibilidad, siguiendo los estándares profesionales más exigentes. Incluye lógica de juego completa, motor de búsqueda, evaluación avanzada y una capa API para integración con interfaces gráficas o clientes externos.

### **Características principales**
- Código en C# 12, .NET 8, compatible multiplataforma.
- Arquitectura modular, principios SOLID y Clean Architecture.
- Generación y validación de movimientos conforme a las reglas FIDE.
- Algoritmos de búsqueda (Minimax, Alpha-Beta, en roadmap: Poda, NNUE, etc.).
- Evaluación de posiciones con múltiples factores (material, movilidad, seguridad del rey...).
- API REST para integración con interfaces gráficas (pendiente de desarrollo).
- Batería de tests automáticos, TDD y cobertura completa (en roadmap).
- Documentación y ejemplos listos para extensión o integración.

### **Arquitectura**
El proyecto se estructura en módulos independientes:
- **Core**: Lógica de ajedrez, reglas, generación y validación de movimientos.
- **Search**: Implementación de algoritmos de búsqueda y heurísticas.
- **Evaluation**: Sistema de evaluación de posiciones.
- **API**: Controladores y endpoints para comunicación externa (REST).
- **UI/Client**: (Futuro) Interfaces gráficas o conectores a UCI/XBoard.
- **Tests**: Pruebas unitarias y de integración.

Se siguen patrones Clean Architecture y DDD para garantizar mantenibilidad y escalabilidad.

### **Tecnologías**
- **Lenguaje**: C# 12, .NET 8 LTS
- **IDE**: Visual Studio Code + extensiones profesionales
- **Testing**: xUnit, FluentAssertions, Moq
- **API**: ASP.NET Core WebAPI (futuro)
- **CI/CD**: GitHub Actions (roadmap)
- **Performance**: BenchmarkDotNet, profiling .NET

### **Roadmap y versiones**
El desarrollo sigue una hoja de ruta incremental:

- **v0.1:** Consola básica, muestra jugadas legales.
- **v0.2:** Añade motor de búsqueda básico y evaluación simple.
- **v0.3:** Arquitectura modular, separación de responsabilidades.
- **v1.0:** Motor completo, integración API y primeras pruebas E2E.
- **Futuro:** Soporte UCI/XBoard, apertura a contribuciones, integración con GUIs externas.

El desarrollo es iterativo y se documentan todos los cambios relevantes en este README.

### **Contribuir**
Se aceptan contribuciones, sugerencias y feedback.  
Por favor, revisa la [Guía de Contribución](CONTRIBUTING.md) (pendiente) y abre issues o pull requests para colaborar.

### **Licencia**
Este proyecto está publicado bajo la licencia **Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0)**.
**Está permitido el uso, copia, modificación y redistribución para cualquier fin NO comercial**.  
**Queda estrictamente prohibido cualquier uso comercial, salvo por el autor o coautores del proyecto.**
Para otros usos, contacta con el titular.

### **Texto legal y resumen en español**

- El texto legalmente vinculante de esta licencia está en inglés y se incluye en el fichero LICENSE de este repositorio:  
  https://creativecommons.org/licenses/by-nc/4.0/legalcode

- Para facilitar la comprensión, existe un resumen oficial en español:  
  https://creativecommons.org/licenses/by-nc/4.0/deed.es

> *Nota: la traducción al español es solo informativa. En caso de discrepancia, prevalece el texto legal en inglés.*

---

### **Contacto / Contact**

**Autor principal / Main Author:**  
Juan Carlos Jiménez Vadillo

- GitHub: [jcjimenezvadillo](https://github.com/jcjimenezvadillo)  

---
