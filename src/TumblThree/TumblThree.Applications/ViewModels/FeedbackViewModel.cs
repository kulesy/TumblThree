﻿using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Waf.Applications;
using System.Windows;
using System.Windows.Input;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Applications.Views;
using TumblThree.Domain;

namespace TumblThree.Applications.ViewModels
{
    [Export]
    public class FeedbackViewModel : ViewModel<IFeedbackView>
    {
        private readonly AsyncDelegateCommand _sendCommand;
        private readonly IApplicationUpdateService _applicationUpdateService;

        private string _message;

        [ImportingConstructor]
        public FeedbackViewModel(IFeedbackView view, IShellService shellService, IApplicationUpdateService applicationUpdateService)
            : base(view)
        {
            ShellService = shellService;
            _sendCommand = new AsyncDelegateCommand(Send);
            _applicationUpdateService = applicationUpdateService;
        }

        public string Name { get; set; }

        public string Email { get; set; }

        public string Message { get; set; }

        public ICommand SendCommand => _sendCommand;

        public IShellService ShellService { get; }

        public void ShowDialog(object owner) => ViewCore.ShowDialog(owner);

        private async Task Send()
        {
            try
            {
                var result = await _applicationUpdateService.SendFeedback(Name, Email, Message);
                if (result)
                    ViewCore.Close();
            }
            catch (Exception ex)
            {
                Logger.Error($"FeedbackViewModel:Send: {ex}");
                MessageBox.Show(Resources.SendFeedbackError, Resources.Warning);
            }
            finally
            {
                await Task.CompletedTask;
            }
        }
    }
}
