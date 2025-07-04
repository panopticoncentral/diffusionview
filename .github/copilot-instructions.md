# GitHub Copilot Instructions for DiffusionView

## Project Overview
DiffusionView is a WinUI 3 application for viewing and managing AI-generated images with Stable Diffusion metadata.

## Key Technologies
- WinUI 3 (Windows App SDK)
- .NET 9
- Entity Framework Core (for database operations)

## Project Structure
- `MainWindow.xaml` and `MainWindow.xaml.cs` - Main application window with photo gallery and single photo view
- `PhotoItem.cs` - Model representing a photo with metadata
- `Service/PhotoService.cs` - Service for managing photos and folders
- `Database/` - Entity Framework models and database context

## Coding Conventions
- Use C# 12 features and modern syntax
- Follow MVVM pattern where applicable
- Use async/await for all I/O operations
- Use nullable reference types
- Prefer expression-bodied members and pattern matching
- Use collection expressions (e.g., `[]` instead of `new List<>()`)

## Important Context
- Photos contain Stable Diffusion generation metadata (prompts, models, LoRAs, etc.)
- The app handles both file operations and database operations
- Focus on performance when dealing with large photo collections
- Error handling should be graceful but minimal logging for now

## When generating code:
- Always use proper async patterns
- Handle exceptions appropriately
- Follow the existing code style and patterns
- Consider performance implications for large datasets
- Use proper disposal patterns for resources