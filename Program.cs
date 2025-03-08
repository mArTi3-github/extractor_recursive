using SevenZip;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static System.Net.WebRequestMethods;

namespace extractor_recursive
{
    internal class Program
    {
        private static Timer _timer;
        private static readonly HashSet<string> _processedArchives = new();
        private static readonly HashSet<string> _failedToExtractFiles = new();
        private static string[] _archiveExtensions = [];
        private static bool filterExtensions = false;
        private static Queue<string> filesToProcess;

        static void Main(string[] args)
        {
            try
            {
                if (args.Length < 1 || args.Length > 2)
                {
                    Usage();
                    return;
                }

                if (args.Length == 2 && !args[0].StartsWith("--extensions="))
                {
                    Usage();
                    return;
                }

                SevenZipBase.SetLibraryPath(Get7zLibraryPath());

                string inputPath;
                string basePathToExtract;

                //TODO: проверить корректность логики распаковки tar.gz

                if (args.Length == 2)
                {
                    inputPath = args[1];
                    _archiveExtensions = args[0]["--extensions=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
                    filterExtensions = true;
                    basePathToExtract = inputPath + ".extracted_by_filter";
                    Console.WriteLine("Режим распаковки: с фильтрацией по выбранным расширениям");
                }
                else
                {
                    inputPath = args[0];
                    basePathToExtract = inputPath + ".extracted_full";
                    Console.WriteLine("Режим распаковки: распаковка всех возможных файлов");
                }
                Console.WriteLine($"[{DateTime.Now.ToShortTimeString()}] Начата обработка архивов по следующему пути: {inputPath}");
                Directory.CreateDirectory(basePathToExtract);

                if (System.IO.File.Exists(inputPath))
                {
                    System.IO.File.Copy(inputPath, basePathToExtract + "\\" + Path.GetFileName(inputPath), true);
                }
                else if (Directory.Exists(inputPath))
                {
                    CopyFilesRecursively(inputPath, basePathToExtract);
                }
                else
                    throw new ArgumentException("Указан неверный путь для распаковки");

                filesToProcess = new Queue<string>(Directory.GetFiles(basePathToExtract, "*", SearchOption.AllDirectories));

                _timer = new Timer(_ => PrintProgress(), null, 10000, 10000);

                Console.WriteLine($"[{DateTime.Now.ToShortTimeString()}] исходные файлы скопированы. Распакованные файлы будут находиться в следующей папке: {basePathToExtract}");

                while (filesToProcess.TryDequeue(out string? currentFile))
                {
                    try
                    {
                        //??? Такое вообще может когда-то случиться? Только есть кто-то или что-то в процессе работы программы начнет удалять файлы 
                        if (!System.IO.File.Exists(currentFile))
                        {
                            Console.WriteLine($"Файл не обнаружен: {currentFile}\nВозможно, он был удален в процессе работы скрипта");
                            continue;
                        }
                            

                        string destPath = currentFile + ".extracted";

                        // Если активен фильтр по расширениям и текущий файл не подходит по расширению
                        if (filterExtensions == true && CheckExtension(currentFile) == false)
                        {
                            continue;
                        }

                        using (SevenZipExtractor extractor = new SevenZipExtractor(currentFile))
                        {
                            // Проверка, является ли файл архивом
                            if (extractor.ArchiveFileNames.Count > 0)
                            {
                                extractor.ExtractArchive(destPath);
                                System.IO.File.Delete(currentFile);
                                _processedArchives.Add(currentFile);
                            }
                        }

                        //Проверка на случай, если в процессе распаковки возникнет исключение и папка не создастся
                        if (Directory.Exists(destPath))
                        {
                            foreach (string newFile in Directory.GetFiles(destPath, "*", SearchOption.AllDirectories))
                            {
                                filesToProcess.Enqueue(newFile);
                            }
                        }
                    }
                    catch (SevenZipException ex)
                    {
                        _failedToExtractFiles.Add(currentFile + $" [SevenZipException: {ex.Message}]");
                    }
                    catch (IOException ex)
                    {
                        _failedToExtractFiles.Add(currentFile + $" [IOException: {ex.Message}]");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _failedToExtractFiles.Add(currentFile + $" [UnauthorizedAccessException: {ex.Message}]");
                    }

                    catch (Exception ex)
                    {
                        _failedToExtractFiles.Add(currentFile + $" [Exception]: {ex.Message}");
                    }
                }

                _timer.Dispose();
                PrintProgress();
                Console.WriteLine($"[{DateTime.Now.ToShortTimeString()}] Обработка завершена.");

                //Запись отладочной информации в текстовые файлы
                System.IO.File.WriteAllLines("_processed_archives.txt", _processedArchives);
                System.IO.File.WriteAllLines("_failed_to_extract_files.txt", _failedToExtractFiles);
                Console.WriteLine("Отладочная информация со списками распакованных и нераспакованных файлов записана в файлы _processed_archives.txt и _failed_to_extract_files.txt");

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Критическая ошибка: {ex.Message}");
            }
        }

        static string Get7zLibraryPath()
        {
            string path = @"C:\Program Files\7-Zip\7z.dll";
            if (!System.IO.File.Exists(path)) throw new FileNotFoundException("7z.dll not found");
            return path;
        }

        //TODO: доабвить функционал, чтобы распаковывать не все архивы, а только подходящие по расширению (добавить в IsArchive?)
        static bool CheckExtension(string path)
        {
            ReadOnlySpan<char> fileName = Path.GetFileName(path).AsSpan();
            foreach (string ext in _archiveExtensions)
            {
                ReadOnlySpan<char> extSpan = ext.AsSpan();
                if (fileName.EndsWith(extSpan, StringComparison.OrdinalIgnoreCase) &&
                    (fileName.Length == ext.Length || fileName[fileName.Length - ext.Length - 1] == '.'))
                {
                    return true;
                }
            }
            return false;
        }

        private static void CopyFilesRecursively(string sourcePath, string targetPath)
        {
            //Now Create all of the directories
            foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));
            }

            //Copy all the files & Replaces any files with the same name
            foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            {
                System.IO.File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
            }
        }

        static void PrintProgress() =>
            Console.WriteLine($"[{DateTime.Now.ToShortTimeString()}] Архивов распаковано: {_processedArchives.Count}, файлов в очереди на распаковку: {filesToProcess.Count}");

        static void Usage() =>
            Console.WriteLine("Использование: extractor_recursive.exe --extensions=ext1,ext2,... input_path  (для распаковки только файлов с указанными расширениями)\n" +
                                             "extractor_recursive.exe input_path (для распаковки всех возможных файлов)");
    }
}