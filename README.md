# NoaChess

[Spanish below]

## Advanced Chess Engine in C# (.NET 10)

NoaChess is a modular, high-performance chess engine built with professional standards. It includes complete chess logic, move generation, advanced search and evaluation, and an API layer for integration with graphical interfaces or external clients.

### **Main Features**
- C# 12, .NET 10, cross-platform.
- Modular architecture, Solid principles and Clean Architecture.
- Legal move generation and validation.
- Search algorithms (Alpha-Beta, roadmap: Pruning, NNUE, etc.).
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
- **Language**: C# 12, .NET 10 LTS
- **IDE**: Visual Studio + extensions
- **Testing**: xUnit, FluentAssertions, Moq
- **API**: ASP.NET Core WebAPI (future)
- **CI/CD**: GitHub Actions (roadmap)
- **Performance**: BenchmarkDotNet, .NET profiling tools

### **Roadmap & Versions**
- **v0.1** — Playable MVP: core rules, alpha-beta, WPF board. ✅
- **v0.2** — Measurable engine: TT, quiescence, move ordering, LMR. ✅
- **v1.0** — Competitive search & stable UCI: PVS, null move, SEE, pawn hash, time management. ✅
- **v1.1** — NPS optimization (magic bitboards, zero-alloc paths), Bullet profile, benchmarks. ✅ (~2070 Elo)
- **v2.0** — NNUE evaluation (in progress next).
- **v2.3** — Polyglot opening books · **v3.0** — Lazy SMP + Syzygy · see the full roadmap.

Full change history in [CHANGELOG.md](CHANGELOG.md).

### **Contributing**
Contributions, suggestions, and feedback are welcome.
Please check the [Contribution Guide](CONTRIBUTING.md) and open issues or pull requests.

### License
This project is licensed under the **Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0)** license.
**Use, copy, modification, and redistribution are allowed for any NON-commercial purpose.**
**Any commercial use is strictly prohibited, except by the author or co-authors of the project.**
For other uses, please contact the owner.

---

## Motor de ajedrez avanzado en C# (.NET 10)

NoaChess es un motor de ajedrez modular, diseñado para alto rendimiento y extensibilidad, siguiendo los estándares profesionales más exigentes. Incluye lógica de juego completa, motor de búsqueda, evaluación avanzada y una capa API para integración con interfaces gráficas o clientes externos.

### **Características principales**
- Código en C# 12, .NET 10, compatible multiplataforma.
- Arquitectura modular, principios Solid y Clean Architecture.
- Generación y validación de movimientos conforme a las reglas FIDE.
- Algoritmos de búsqueda (Alpha-Beta, en roadmap: Poda, NNUE, etc.).
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
- **Lenguaje**: C# 12, .NET 10 LTS
- **IDE**: Visual Studio + extensiones
- **Testing**: xUnit, FluentAssertions, Moq
- **API**: ASP.NET Core WebAPI (futuro)
- **CI/CD**: GitHub Actions (roadmap)
- **Performance**: BenchmarkDotNet, profiling .NET

### **Roadmap y versiones**
- **v0.1** — MVP jugable: reglas, alpha-beta, tablero WPF. ✅
- **v0.2** — Motor medible: TT, quiescence, move ordering, LMR. ✅
- **v1.0** — Search competitivo y UCI estable: PVS, null move, SEE, pawn hash, gestión de tiempo. ✅
- **v1.1** — Optimización NPS (magic bitboards, cero allocations), perfil Bullet, benchmarks. ✅ (~2070 Elo)
- **v2.0** — Evaluación NNUE (siguiente, en preparación).
- **v2.3** — Libros de apertura Polyglot · **v3.0** — Lazy SMP + Syzygy · ver roadmap completo.

Historial de cambios completo en [CHANGELOG.md](CHANGELOG.md).

### **Contribuir**
Se aceptan contribuciones, sugerencias y feedback.
Por favor, revisa la [Guía de Contribución](CONTRIBUTING.md) y abre issues o pull requests para colaborar.

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
